using System.Net;

namespace VirtualLANPlatform.Core.Networking;

public enum NatResult { Success, UPnPFailed, NoPublicIP }

/// <summary>
/// Live status of NAT traversal — shown in the debug panel.
/// </summary>
public class NatStatus
{
    public string? LocalIP      { get; set; }
    public string? PublicIP     { get; set; }
    public ushort? LocalPort    { get; set; }
    public ushort? ExternalPort { get; set; }
    public bool    UPnPFound    { get; set; }
    public bool    UPnPMapped   { get; set; }
    public string  NatType      { get; set; } = "در حال بررسی...";
}

/// <summary>
/// Orchestrates NAT traversal for the Host role.
///
/// Strategy (in order):
///   1. Get public IP from external service
///   2. Attempt UPnP port mapping on the router
///   3. Report status — guest connectivity is validated when they connect
/// </summary>
public sealed class NatTraversal(UPnPManager upnp, PublicIPDiscovery ipDiscovery)
{
    public NatStatus Status { get; } = new();

    /// <summary>
    /// Sets local address immediately and runs public-IP + UPnP in the background.
    /// Returns instantly so the Host UI is never blocked.
    /// </summary>
    public Task<NatResult> PrepareHostAsync(
        ushort localPort, string localIp, CancellationToken ct = default)
    {
        Status.LocalIP   = localIp;
        Status.LocalPort = localPort;
        Status.NatType   = "در حال بررسی (background)...";

        // Fire-and-forget background discovery — capped at 10 s total
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var token = cts.Token;

                IPAddress? publicIp = await ipDiscovery.GetPublicIPAsync(token)
                    .ConfigureAwait(false);
                Status.PublicIP = publicIp?.ToString() ?? "کشف نشد";

                if (publicIp != null)
                {
                    Status.UPnPFound = await upnp.TryDiscoverAsync(token).ConfigureAwait(false);
                    if (Status.UPnPFound)
                    {
                        Status.UPnPMapped = await upnp.AddPortMappingAsync(
                            localPort, localPort, localIp, "Virtual LAN Platform", token)
                            .ConfigureAwait(false);
                        Status.ExternalPort = localPort;
                        Status.NatType = Status.UPnPMapped
                            ? "UPnP — Port Mapping موفق"
                            : "UPnP پیدا شد اما Mapping شکست خورد";
                    }
                    else
                    {
                        Status.NatType = "UPnP Gateway شناسایی نشد";
                    }
                }
                else
                {
                    Status.NatType = "Public IP کشف نشد — LAN only";
                }
            }
            catch { Status.NatType = "NAT discovery لغو شد"; }
        }, ct);

        return Task.FromResult(NatResult.Success);
    }

    /// <summary>
    /// Human-readable Persian failure guidance shown to the user when
    /// a Guest cannot connect.
    /// </summary>
    public static string GetFailureGuidance() =>
        """
        اتصال برقرار نشد. راه‌حل‌های پیشنهادی:

        ۱. در تنظیمات روتر، UPnP را فعال کنید
        ۲. بررسی کنید Windows Firewall برنامه را مسدود نکرده باشد
        ۳. اگر هر دو کاربر در یک شبکه هستند، از IP محلی استفاده کنید
        ۴. با اپراتور اینترنت بررسی کنید که پشت CGNAT هستید یا نه
        ۵. در تنظیمات پیشرفته برنامه Port Forwarding دستی تنظیم کنید
        """;
}
