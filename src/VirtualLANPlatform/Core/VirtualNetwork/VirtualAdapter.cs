using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VirtualLANPlatform.Core.VirtualNetwork;

/// <summary>
/// Manages the WinTun virtual network adapter lifecycle.
/// One adapter per process; must run as Administrator.
/// </summary>
public sealed class VirtualAdapter : IDisposable
{
    private const uint   RingCapacity   = 0x400000; // 4 MB ring buffer
    private const string AdapterName    = "VirtualLAN";
    private const string TunnelType     = "VirtualLAN";

    private static readonly Guid AdapterGuid =
        new("A7B3C2D1-E4F5-6789-ABCD-EF0123456789");

    private nint _adapter;
    private nint _session;
    private bool _disposed;

    public bool  IsOpen      => _adapter != 0;
    public bool  HasSession  => _session != 0;
    public ulong Luid        { get; private set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Creates (or reopens) the WinTun adapter and returns true on success.</summary>
    public bool Open()
    {
        if (IsOpen) return true;

        _adapter = WinTunNative.WintunCreateAdapter(AdapterName, TunnelType, AdapterGuid);
        if (_adapter == 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"WintunCreateAdapter شکست خورد (کد: {err}). برنامه باید با دسترسی Administrator اجرا شود.");
        }

        WinTunNative.WintunGetAdapterLUID(_adapter, out ulong luid);
        Luid = luid;
        return true;
    }

    /// <summary>Starts a packet I/O session on the open adapter.</summary>
    public void StartSession()
    {
        if (!IsOpen) throw new InvalidOperationException("آداپتور باید ابتدا Open() شود.");
        if (HasSession) return;

        _session = WinTunNative.WintunStartSession(_adapter, RingCapacity);
        if (_session == 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"WintunStartSession شکست خورد (کد: {err}).");
        }
    }

    // ── Read wait event ───────────────────────────────────────────────────────

    /// <summary>Returns the Win32 event HANDLE that WinTun signals when a packet arrives.</summary>
    public nint GetReadWaitEvent()
    {
        if (!HasSession) throw new InvalidOperationException("Session فعال نیست.");
        return WinTunNative.WintunGetReadWaitEvent(_session);
    }

    // ── Receive ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the next received IP packet, or null if the ring is empty.
    /// The caller MUST call <see cref="ReleaseReceivePacket"/> after processing.
    /// </summary>
    public unsafe bool TryReceivePacket(out ReadOnlySpan<byte> packet, out byte* rawPtr)
    {
        rawPtr = null;
        packet = default;
        if (!HasSession) return false;

        byte* ptr = WinTunNative.WintunReceivePacket(_session, out uint size);
        if (ptr == null)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == WinTunNative.ERROR_NO_MORE_ITEMS) return false;
            return false; // Other transient errors — just skip
        }

        rawPtr = ptr;
        packet = new ReadOnlySpan<byte>(ptr, (int)size);
        return true;
    }

    public unsafe void ReleaseReceivePacket(byte* ptr)
    {
        if (HasSession && ptr != null)
            WinTunNative.WintunReleaseReceivePacket(_session, ptr);
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    /// <summary>Injects an IP packet into the virtual adapter (appears as received traffic to the OS).</summary>
    public unsafe bool SendPacket(ReadOnlySpan<byte> ipPacket)
    {
        if (!HasSession) return false;
        if (ipPacket.IsEmpty || ipPacket.Length > 65535) return false;

        byte* buf = WinTunNative.WintunAllocateSendPacket(_session, (uint)ipPacket.Length);
        if (buf == null) return false; // ring full — drop packet

        ipPacket.CopyTo(new Span<byte>(buf, ipPacket.Length));
        WinTunNative.WintunSendPacket(_session, buf);
        return true;
    }

    // ── Driver version ────────────────────────────────────────────────────────

    public static uint GetDriverVersion() =>
        WinTunNative.WintunGetRunningDriverVersion();

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_session != 0)
        {
            WinTunNative.WintunEndSession(_session);
            _session = 0;
        }

        if (_adapter != 0)
        {
            WinTunNative.WintunCloseAdapter(_adapter);
            _adapter = 0;
        }
    }
}
