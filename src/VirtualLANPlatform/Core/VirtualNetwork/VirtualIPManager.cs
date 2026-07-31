using System.Diagnostics;
using System.Net;

namespace VirtualLANPlatform.Core.VirtualNetwork;

/// <summary>
/// Assigns and removes IPv4 addresses on the WinTun adapter using netsh.
///
/// Subnet: 10.77.0.0/16
///   Host  → 10.77.0.1
///   Guest → 10.77.0.2 … 10.77.255.254  (allocated by Host in Phase 3)
/// </summary>
public sealed class VirtualIPManager : IDisposable
{
    private const string AdapterName = "VirtualLAN";
    private const string SubnetMask  = "255.255.0.0";

    private IPAddress? _assignedIP;
    private bool _disposed;

    public IPAddress?  AssignedIP => _assignedIP;
    public static string SubnetDescription => "10.77.0.0/16";

    // ── Assign ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assigns a virtual IP to the WinTun adapter via netsh.
    /// <paramref name="isHost"/> true → 10.77.0.1, false → 10.77.0.<paramref name="guestOctetLow"/>
    /// </summary>
    public IPAddress AssignIP(ulong luid, bool isHost, byte guestOctetLow = 2)
    {
        // Wait up to 8 s for adapter to appear and leave "Media Disconnected" state
        WaitForAdapter(timeoutSeconds: 8);

        string ip = isHost ? "10.77.0.1" : $"10.77.0.{guestOctetLow}";

        // Ensure the interface is admin-enabled
        RunNetsh($"interface set interface \"{AdapterName}\" admin=enabled", ignoreExit: true);
        Thread.Sleep(100);

        // "set address static" replaces any existing static IP in one step (no delete needed)
        // Retry up to 5 times in case the adapter is still initialising
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            var psi = new ProcessStartInfo("netsh",
                $"interface ip set address \"{AdapterName}\" static {ip} {SubnetMask}")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("netsh را نمی‌توان اجرا کرد.");

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);

            if (p.ExitCode == 0) { lastEx = null; break; }

            string msg = (stdout + " " + stderr).Trim();

            // "already exists" means the address is already correctly configured
            if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                lastEx = null;
                break;
            }

            lastEx = new InvalidOperationException($"netsh شکست خورد (کد {p.ExitCode}): {msg}");
            if (attempt < 5) Thread.Sleep(800);
        }

        if (lastEx != null) throw lastEx;

        _assignedIP = IPAddress.Parse(ip);
        return _assignedIP;
    }

    // ── Release ───────────────────────────────────────────────────────────────

    public void Release()
    {
        if (_assignedIP == null) return;
        // Delete the specific assigned address (the "all" parameter is not valid netsh syntax)
        RunNetsh($"interface ip delete address \"{AdapterName}\" addr={_assignedIP}", ignoreExit: true);
        _assignedIP = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WaitForAdapter(int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            string output = RunNetsh($"interface show interface \"{AdapterName}\"",
                ignoreExit: true);
            // Wait until adapter appears AND is no longer "Media Disconnected"
            if (output.Contains("VirtualLAN", StringComparison.OrdinalIgnoreCase) &&
                !output.Contains("Media Disconnected", StringComparison.OrdinalIgnoreCase))
                return;
            Thread.Sleep(300);
        }
        // Non-fatal: proceed and let the retry loop in AssignIP handle any remaining delay
    }

    private static string RunNetsh(string args, bool ignoreExit)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("netsh را نمی‌توان اجرا کرد.");

        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(5000);

        if (!ignoreExit && p.ExitCode != 0)
        {
            string msg = (stdout + " " + stderr).Trim();
            throw new InvalidOperationException(
                $"netsh شکست خورد (کد {p.ExitCode}): {msg}");
        }

        return stdout + stderr;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
