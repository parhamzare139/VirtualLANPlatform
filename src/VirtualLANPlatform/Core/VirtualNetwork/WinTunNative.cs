using System.Runtime.InteropServices;

namespace VirtualLANPlatform.Core.VirtualNetwork;

/// <summary>
/// P/Invoke bindings for wintun.dll (MIT License — WireGuard Project).
/// Attribution: https://www.wintun.net
/// </summary>
internal static unsafe class WinTunNative
{
    private const string Dll = "wintun.dll";

    // ── Adapter lifecycle ─────────────────────────────────────────────────────

    /// <summary>Creates a new WinTun adapter or opens an existing one.</summary>
    [DllImport(Dll, EntryPoint = "WintunCreateAdapter", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint WintunCreateAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string tunnelType,
        in Guid requestedGuid);

    /// <summary>Releases a WinTun adapter handle (does NOT delete the adapter from the system).</summary>
    [DllImport(Dll, EntryPoint = "WintunCloseAdapter", SetLastError = true)]
    internal static extern void WintunCloseAdapter(nint adapter);

    /// <summary>Deletes the adapter from the system.</summary>
    [DllImport(Dll, EntryPoint = "WintunDeleteAdapter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WintunDeleteAdapter(nint adapter);

    // ── Session lifecycle ─────────────────────────────────────────────────────

    /// <summary>Starts a packet I/O session. Capacity is the ring-buffer size in bytes (must be power of 2, 0x20000–0x4000000).</summary>
    [DllImport(Dll, EntryPoint = "WintunStartSession", SetLastError = true)]
    internal static extern nint WintunStartSession(nint adapter, uint capacity);

    /// <summary>Ends the packet I/O session.</summary>
    [DllImport(Dll, EntryPoint = "WintunEndSession")]
    internal static extern void WintunEndSession(nint session);

    // ── Packet receive ────────────────────────────────────────────────────────

    /// <summary>Returns a pointer to the next received packet or NULL (ERROR_NO_MORE_ITEMS) if ring is empty.</summary>
    [DllImport(Dll, EntryPoint = "WintunReceivePacket", SetLastError = true)]
    internal static extern byte* WintunReceivePacket(nint session, out uint packetSize);

    /// <summary>Releases a packet buffer obtained from WintunReceivePacket.</summary>
    [DllImport(Dll, EntryPoint = "WintunReleaseReceivePacket")]
    internal static extern void WintunReleaseReceivePacket(nint session, byte* packet);

    /// <summary>Returns a waitable HANDLE that is signalled when packets are available.</summary>
    [DllImport(Dll, EntryPoint = "WintunGetReadWaitEvent")]
    internal static extern nint WintunGetReadWaitEvent(nint session);

    // ── Packet send ───────────────────────────────────────────────────────────

    /// <summary>Allocates a send buffer. Returns NULL if ring is full (ERROR_BUFFER_OVERFLOW).</summary>
    [DllImport(Dll, EntryPoint = "WintunAllocateSendPacket", SetLastError = true)]
    internal static extern byte* WintunAllocateSendPacket(nint session, uint packetSize);

    /// <summary>Commits a previously allocated packet for sending.</summary>
    [DllImport(Dll, EntryPoint = "WintunSendPacket")]
    internal static extern void WintunSendPacket(nint session, byte* packet);

    // ── Adapter info ──────────────────────────────────────────────────────────

    /// <summary>Returns the adapter's NET_LUID (needed to set IP address via IP Helper API).</summary>
    [DllImport(Dll, EntryPoint = "WintunGetAdapterLUID")]
    internal static extern void WintunGetAdapterLUID(nint adapter, out ulong luid);

    /// <summary>Returns the running WinTun driver version (0 if not running).</summary>
    [DllImport(Dll, EntryPoint = "WintunGetRunningDriverVersion")]
    internal static extern uint WintunGetRunningDriverVersion();

    // ── Win32 helpers ─────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    internal const uint WAIT_OBJECT_0   = 0x00000000;
    internal const uint WAIT_TIMEOUT    = 0x00000102;
    internal const uint INFINITE        = 0xFFFFFFFF;
    internal const int  ERROR_NO_MORE_ITEMS   = 259;
    internal const int  ERROR_BUFFER_OVERFLOW = 111;

    // ── IP Helper API — adapter LUID → interface index ────────────────────────

    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint ConvertInterfaceLuidToIndex(in ulong luid, out uint index);
}
