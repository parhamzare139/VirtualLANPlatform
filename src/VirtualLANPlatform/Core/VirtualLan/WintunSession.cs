using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VirtualLANPlatform.Core.VirtualLan;

/// <summary>
/// Managed lifetime around a single Wintun adapter + session: creates the adapter,
/// assigns its IPv4 address, pumps inbound packets on a background thread, and lets
/// callers push outbound packets. Everything is best-effort — any failure raises
/// <see cref="Failed"/> and the session tears itself down.
/// </summary>
public sealed class WintunSession : IDisposable
{
    // Stable identity so repeated runs reuse the same adapter instead of leaving orphans.
    private static readonly Guid AdapterGuid = new("d3a8f21c-4b7e-4c9a-9f2d-11a2b3c4d5e6");
    private const string AdapterName = "VirtualLAN";
    private const string TunnelType  = "VirtualLANPlatform";

    private IntPtr _adapter;
    private IntPtr _session;
    private IntPtr _readEvent;
    private Thread? _readThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    /// <summary>Raised (on the pump thread) for every inbound IP packet from the OS.</summary>
    public event Action<byte[]>? PacketReceived;

    /// <summary>Raised once on a fatal error; the session is unusable afterwards.</summary>
    public event Action<string>? Failed;

    public bool   IsRunning => _running;
    public string AssignedIp { get; private set; } = "";

    /// <summary>Creates the adapter, sets <paramref name="ipv4"/>/<paramref name="mask"/>, and starts pumping.</summary>
    public bool Start(string ipv4, string mask)
    {
        try
        {
            _adapter = Wintun.WintunCreateAdapter(AdapterName, TunnelType, AdapterGuid);
            if (_adapter == IntPtr.Zero)
            {
                Failed?.Invoke($"ساخت آداپتور مجازی ناموفق بود (کد {Marshal.GetLastWin32Error()}) — درایور Wintun بارگذاری نشد؟");
                return false;
            }

            _session = Wintun.WintunStartSession(_adapter, Wintun.RingCapacity);
            if (_session == IntPtr.Zero)
            {
                Failed?.Invoke($"شروع session آداپتور مجازی ناموفق بود (کد {Marshal.GetLastWin32Error()})");
                Cleanup();
                return false;
            }

            _readEvent = Wintun.WintunGetReadWaitEvent(_session);

            // Adapter is up now — assign its address and trim MTU so a tunnelled
            // 1500-byte frame still fits inside one underlay UDP datagram. The first
            // "set address" can race the OS finishing interface plumbing, so retry once.
            if (!SetAddress(ipv4, mask))
            {
                Thread.Sleep(700);
                SetAddress(ipv4, mask);
            }
            Netsh($"interface ipv4 set subinterface \"{AdapterName}\" mtu=1400 store=active");
            AssignedIp = ipv4;

            _cts        = new CancellationTokenSource();
            _running    = true;
            _readThread = new Thread(() => ReadLoop(_cts.Token))
            {
                IsBackground = true,
                Name         = "VLAN-TunRead"
            };
            _readThread.Start();
            return true;
        }
        catch (DllNotFoundException)
        {
            Failed?.Invoke("wintun.dll پیدا نشد.");
            Cleanup();
            return false;
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"خطای آداپتور مجازی: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    /// <summary>Injects one IP packet into the local OS stack.</summary>
    public void WritePacket(byte[] packet, int length)
    {
        if (!_running || _session == IntPtr.Zero || length <= 0 || length > ushort.MaxValue) return;
        try
        {
            IntPtr buf = Wintun.WintunAllocateSendPacket(_session, (uint)length);
            if (buf == IntPtr.Zero) return; // send ring full — drop, like a real congested link
            Marshal.Copy(packet, 0, buf, length);
            Wintun.WintunSendPacket(_session, buf);
        }
        catch { /* transient — drop this packet */ }
    }

    private void ReadLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IntPtr p;
            uint size;
            try { p = Wintun.WintunReceivePacket(_session, out size); }
            catch { break; }

            if (p != IntPtr.Zero)
            {
                if (size is > 0 and <= ushort.MaxValue)
                {
                    var buf = new byte[size];
                    Marshal.Copy(p, buf, 0, (int)size);
                    Wintun.WintunReleaseReceivePacket(_session, p);
                    try { PacketReceived?.Invoke(buf); } catch { }
                }
                else
                {
                    Wintun.WintunReleaseReceivePacket(_session, p);
                }
                continue;
            }

            uint err = (uint)Marshal.GetLastWin32Error();
            if (err == Wintun.ERROR_NO_MORE_ITEMS)
            {
                if (_readEvent != IntPtr.Zero)
                    Wintun.WaitForSingleObject(_readEvent, 300);
                else
                    Thread.Sleep(5);
            }
            else
            {
                if (!token.IsCancellationRequested)
                    Failed?.Invoke($"خواندن از آداپتور مجازی متوقف شد (کد {err})");
                break;
            }
        }
    }

    private static bool SetAddress(string ipv4, string mask)
    {
        int code = Netsh($"interface ip set address name=\"{AdapterName}\" static {ipv4} {mask}");
        return code == 0;
    }

    private static int Netsh(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            })!;
            return p.WaitForExit(5000) ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    private void Cleanup()
    {
        if (_session != IntPtr.Zero) { try { Wintun.WintunEndSession(_session); } catch { } _session = IntPtr.Zero; }
        if (_adapter != IntPtr.Zero) { try { Wintun.WintunCloseAdapter(_adapter); } catch { } _adapter = IntPtr.Zero; }
        _readEvent = IntPtr.Zero;
    }

    public void Dispose()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _readThread?.Join(1500); } catch { }
        _cts?.Dispose();
        _cts = null;
        Cleanup();
        AssignedIp = "";
    }
}
