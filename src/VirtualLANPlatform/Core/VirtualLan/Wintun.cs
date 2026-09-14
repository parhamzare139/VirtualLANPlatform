using System.Runtime.InteropServices;

namespace VirtualLANPlatform.Core.VirtualLan;

/// <summary>
/// Raw P/Invoke surface for wintun.dll (WireGuard's userspace TUN driver, MIT), plus the
/// slice of the Windows IP Helper API used to configure the adapter it creates.
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

    /// <summary>
    /// Opens an existing adapter by name without creating one. Returns NULL when no
    /// adapter by that name exists — which is how uninstall tells "already gone" apart
    /// from a real failure, instead of creating an adapter just to delete it.
    /// </summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WintunOpenAdapter(string name);

    /// <summary>Closing the handle also removes the adapter from the system.</summary>
    [DllImport(Dll, SetLastError = true)]
    public static extern void WintunCloseAdapter(IntPtr adapter);

    /// <summary>
    /// Unregisters the Wintun driver itself. Only succeeds once every adapter is gone,
    /// so it is the last step of an uninstall and a no-op at any other time.
    /// </summary>
    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WintunDeleteDriver();

    /// <summary>
    /// Writes the adapter's NET_LUID — the only stable handle onto the Windows interface
    /// behind it. Everything that configures the adapter (address, MTU, metric, firewall
    /// scope) keys off this rather than the adapter's display name, which Windows will
    /// silently rename to "VirtualLAN 2" when a stale one is still present.
    /// The C function returns void, so there is no status to check.
    /// </summary>
    [DllImport(Dll)]
    public static extern void WintunGetAdapterLUID(IntPtr adapter, out ulong luid);

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

/// <summary>
/// The IP Helper (iphlpapi) calls used to give the Wintun adapter an address.
///
/// This replaced shelling out to <c>netsh interface ip set address name="VirtualLAN"</c>,
/// which had two failure modes that both ended with a virtual LAN that looked configured
/// and carried no traffic: it addresses the interface by display name, which is wrong the
/// moment Windows renames a duplicate, and it races the OS finishing interface plumbing so
/// the first call after adapter creation often failed outright. These APIs take the LUID
/// and report a real status code.
/// </summary>
internal static class IpHelper
{
    private const string Dll = "iphlpapi.dll";

    public const ushort AF_INET = 2;

    public const uint NO_ERROR                    = 0;
    public const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;
    public const uint ERROR_NOT_FOUND             = 1168;

    /// <summary>
    /// SOCKADDR_INET is a union whose largest arm is sockaddr_in6 (28 bytes). The explicit
    /// size matters: declaring only the IPv4 arm would make the struct 16 bytes and shift
    /// every field after it in MIB_UNICASTIPADDRESS_ROW.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct SockAddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(2)] public ushort Port;
        /// <summary>IPv4 address in network byte order.</summary>
        [FieldOffset(4)] public uint   Ipv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicastIpAddressRow
    {
        public SockAddrInet Address;
        public ulong        InterfaceLuid;
        public uint         InterfaceIndex;
        public int          PrefixOrigin;
        public int          SuffixOrigin;
        public uint         ValidLifetime;
        public uint         PreferredLifetime;
        public byte         OnLinkPrefixLength;
        public byte         SkipAsSource;
        public int          DadState;
        public uint         ScopeId;
        public long         CreationTimeStamp;
    }

    [DllImport(Dll)]
    public static extern void InitializeUnicastIpAddressEntry(ref UnicastIpAddressRow row);

    [DllImport(Dll)]
    public static extern uint CreateUnicastIpAddressEntry(ref UnicastIpAddressRow row);

    [DllImport(Dll)]
    public static extern uint DeleteUnicastIpAddressEntry(ref UnicastIpAddressRow row);

    /// <summary>Allocates a table of every unicast address on the machine; free it with FreeMibTable.</summary>
    [DllImport(Dll)]
    public static extern uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [DllImport(Dll)]
    public static extern void FreeMibTable(IntPtr table);

    /// <summary>
    /// Resolves the LUID to the interface's real alias ("VirtualLAN", or "VirtualLAN 2"
    /// when a stale adapter kept the first name). Callers that still need netsh — MTU,
    /// metric, firewall scope — must address the interface by this, never by the name we
    /// asked Wintun for.
    /// </summary>
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern uint ConvertInterfaceLuidToAlias(
        in ulong luid,
        [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder alias,
        nuint length);

    [DllImport(Dll)]
    public static extern uint ConvertInterfaceLuidToIndex(in ulong luid, out uint index);
}
