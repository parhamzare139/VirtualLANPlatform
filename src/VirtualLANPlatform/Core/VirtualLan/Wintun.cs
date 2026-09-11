using System.Runtime.InteropServices;

namespace VirtualLANPlatform.Core.VirtualLan;

/// <summary>
/// Raw P/Invoke surface for wintun.dll (WireGuard's userspace TUN driver, MIT).
/// The DLL ships next to the executable (see the project's Assets\wintun.dll copy step).
///
/// Ref: https://git.zx2c4.com/wintun/about/  — API is ABI-stable since 0.10.
/// </summary>
internal static class Wintun
{
    private const string Dll = "wintun.dll";

    public const uint ERROR_NO_MORE_ITEMS = 259;
    public const uint WAIT_OBJECT_0       = 0;
    public const uint WAIT_TIMEOUT        = 258;

    // Ring capacity: must be a power of two between 128 KiB and 64 MiB.
    public const uint RingCapacity = 0x40_0000; // 4 MiB

    /// <summary>Creates (or opens) a Wintun adapter. <paramref name="requestedGuid"/> makes the
    /// adapter identity stable across runs. Returns NULL on failure (check Marshal.GetLastWin32Error).</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WintunCreateAdapter(string name, string tunnelType, in Guid requestedGuid);

    [DllImport(Dll, SetLastError = true)]
    public static extern void WintunCloseAdapter(IntPtr adapter);

    [DllImport(Dll)]
    public static extern uint WintunGetRunningDriverVersion();

    /// <summary>Starts a send/receive session on the adapter. Returns NULL on failure.</summary>
    [DllImport(Dll, SetLastError = true)]
    public static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [DllImport(Dll)]
    public static extern void WintunEndSession(IntPtr session);

    /// <summary>Auto-reset event that is signalled while packets are queued for reading.</summary>
    [DllImport(Dll)]
    public static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

    /// <summary>Returns a pointer to the next inbound packet (release it when done), or NULL.
    /// NULL with GetLastError == ERROR_NO_MORE_ITEMS means "queue empty, wait on the event".</summary>
    [DllImport(Dll, SetLastError = true)]
    public static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    [DllImport(Dll)]
    public static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    /// <summary>Reserves space in the send ring for a packet of <paramref name="packetSize"/> bytes.
    /// Fill the returned buffer, then hand it to WintunSendPacket. NULL if the ring is full.</summary>
    [DllImport(Dll, SetLastError = true)]
    public static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    [DllImport(Dll)]
    public static extern void WintunSendPacket(IntPtr session, IntPtr packet);

    // ── kernel32 (waiting on the read event) ────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
