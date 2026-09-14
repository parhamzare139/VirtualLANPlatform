using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

using VirtualLANPlatform.UI.Localization;

namespace VirtualLANPlatform.Core.VirtualLan;

/// <summary>
/// Managed lifetime around a single Wintun adapter + session: creates the adapter,
/// assigns its IPv4 address, pumps inbound packets on a background thread, and lets
/// callers push outbound packets. Any fatal failure raises <see cref="Failed"/> and the
/// session tears itself down.
/// </summary>
public sealed class WintunSession : IDisposable
{
    // Stable identity so repeated runs reuse the same adapter instead of leaving orphans.
    private static readonly Guid AdapterGuid = new("d3a8f21c-4b7e-4c9a-9f2d-11a2b3c4d5e6");
    private const string AdapterName = "VirtualLAN";
    private const string TunnelType  = "VirtualLANPlatform";

    /// <summary>
    /// Tunnel MTU. Every inner packet is carried inside AES-GCM (28 bytes) + our frame
    /// header (8) + a LiteNetLib channel header + UDP/IP (28) on the underlay, so the old
    /// 1400 left roughly nothing spare: any underlay below a clean 1500 — PPPoE, a VPN,
    /// most mobile tethers — silently black-holed full-size packets. 1280 is the
    /// conventional safe floor and leaves real headroom.
    /// </summary>
    public const int TunnelMtu = 1280;

    private IntPtr _adapter;
    private IntPtr _session;
    private IntPtr _readEvent;
    private ulong  _luid;
    private Thread? _readThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    /// <summary>Raised (on the pump thread) for every inbound IP packet from the OS.</summary>
    public event Action<byte[]>? PacketReceived;

    /// <summary>Raised once on a fatal error; the session is unusable afterwards.</summary>
    public event Action<string>? Failed;

    public bool   IsRunning  => _running;
    public string AssignedIp { get; private set; } = "";

    /// <summary>The real Windows interface alias — the name anything external must use.</summary>
    public string InterfaceAlias { get; private set; } = "";

    /// <summary>Creates the adapter, assigns the address, and starts pumping packets.</summary>
    public bool Start(string ipv4, int prefixLength)
    {
        try
        {
            _adapter = Wintun.WintunCreateAdapter(AdapterName, TunnelType, AdapterGuid);
            if (_adapter == IntPtr.Zero)
            {
                Failed?.Invoke(Loc.T("Wt_CreateFail", Marshal.GetLastWin32Error()));
                return false;
            }

            Wintun.WintunGetAdapterLUID(_adapter, out _luid);
            InterfaceAlias = ResolveAlias(_luid);

            // Address before session: a session pumping packets on an unaddressed
            // interface looks alive and moves nothing, which is exactly how this
            // used to fail — silently, while the UI reported an IP.
            if (!AssignAddress(ipv4, prefixLength, out string addrError))
            {
                Failed?.Invoke(addrError);
                Cleanup();
                return false;
            }

            _session = Wintun.WintunStartSession(_adapter, Wintun.RingCapacity);
            if (_session == IntPtr.Zero)
            {
                Failed?.Invoke(Loc.T("Wt_SessionFail", Marshal.GetLastWin32Error()));
                Cleanup();
                return false;
            }

            _readEvent = Wintun.WintunGetReadWaitEvent(_session);
            AssignedIp = ipv4;

            ApplyInterfaceTuning();

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
            Failed?.Invoke(Loc.T("Wt_DllMissing"));
            Cleanup();
            return false;
        }
        catch (Exception ex)
        {
            Failed?.Invoke(Loc.T("Wt_Generic", ex.Message));
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Removes the virtual adapter and unregisters the driver. Called by the uninstaller
    /// through the <c>--remove-adapter</c> switch: nothing else can do this, so without
    /// it uninstalling leaves a phantom network adapter in Windows forever, with the app
    /// that explains it already deleted.
    /// </summary>
    /// <returns>A short human-readable account of what was removed, for the log.</returns>
    public static string RemoveAdapterAndDriver()
    {
        string adapterResult;
        try
        {
            // Open, never create — otherwise an uninstall on a machine that never used
            // the feature would conjure an adapter just to delete it.
            IntPtr existing = Wintun.WintunOpenAdapter(AdapterName);
            if (existing != IntPtr.Zero)
            {
                Wintun.WintunCloseAdapter(existing); // closing removes it
                adapterResult = "adapter removed";
            }
            else adapterResult = "no adapter present";
        }
        catch (DllNotFoundException) { return "wintun.dll not found"; }
        catch (Exception ex)         { return $"adapter removal failed: {ex.Message}"; }

        string driverResult;
        try
        {
            // Fails while any Wintun adapter still exists — including one belonging to a
            // different application, which is exactly when it should be left alone.
            driverResult = Wintun.WintunDeleteDriver() ? "driver removed" : "driver kept (still in use)";
        }
        catch (Exception ex) { driverResult = $"driver removal failed: {ex.Message}"; }

        return $"{adapterResult}; {driverResult}";
    }

    // ── Address configuration ────────────────────────────────────────────────

    /// <summary>
    /// Puts the address on the interface, clearing whatever a previous run left behind
    /// first. Returns false with a reason rather than reporting success on a
    /// half-configured interface.
    /// </summary>
    private bool AssignAddress(string ipv4, int prefixLength, out string error)
    {
        error = "";

        if (!IPAddress.TryParse(ipv4, out var parsed) ||
            parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            error = Loc.T("Wt_BadIp", ipv4);
            return false;
        }

        RemoveExistingAddresses();

        var row = new IpHelper.UnicastIpAddressRow();
        IpHelper.InitializeUnicastIpAddressEntry(ref row);
        row.InterfaceLuid      = _luid;
        row.Address.Family     = IpHelper.AF_INET;
        row.Address.Ipv4       = BitConverter.ToUInt32(parsed.GetAddressBytes(), 0); // already network order
        row.OnLinkPrefixLength = (byte)prefixLength;
        // DadState Preferred: there is no L2 on a TUN interface to run duplicate-address
        // detection over, and leaving it Tentative keeps the address unusable for seconds
        // after it appears.
        row.DadState = 4;

        uint status = IpHelper.CreateUnicastIpAddressEntry(ref row);
        if (status is IpHelper.NO_ERROR or IpHelper.ERROR_OBJECT_ALREADY_EXISTS)
            return true;

        error = Loc.T("Wt_AssignFail", status);
        return false;
    }

    /// <summary>
    /// Drops every IPv4 address already on our interface. The adapter is reused by GUID
    /// across runs, so without this a stale address survives on it and the interface ends
    /// up multi-homed with one address nobody on the virtual LAN routes to.
    /// </summary>
    private void RemoveExistingAddresses()
    {
        IntPtr table = IntPtr.Zero;
        try
        {
            if (IpHelper.GetUnicastIpAddressTable(IpHelper.AF_INET, out table) != IpHelper.NO_ERROR ||
                table == IntPtr.Zero)
                return;

            // MIB_UNICASTIPADDRESS_TABLE is { ULONG NumEntries; ROW Table[]; } — the rows
            // start at offset 8 because the array is 8-byte aligned on x64.
            uint count   = (uint)Marshal.ReadInt32(table);
            int  rowSize = Marshal.SizeOf<IpHelper.UnicastIpAddressRow>();

            for (uint i = 0; i < count; i++)
            {
                IntPtr p = IntPtr.Add(table, 8 + (int)i * rowSize);
                var row  = Marshal.PtrToStructure<IpHelper.UnicastIpAddressRow>(p);
                if (row.InterfaceLuid != _luid) continue;
                IpHelper.DeleteUnicastIpAddressEntry(ref row);
            }
        }
        catch { /* best effort — CreateUnicastIpAddressEntry still reports the real outcome */ }
        finally
        {
            if (table != IntPtr.Zero) IpHelper.FreeMibTable(table);
        }
    }

    /// <summary>
    /// MTU and interface metric. Quality settings rather than correctness ones, so a
    /// failure here is not fatal — but the low metric matters for games: Windows sends a
    /// 255.255.255.255 broadcast out the lowest-metric interface, and LAN game discovery
    /// is broadcast. Addressed by the resolved alias, never by the requested name.
    /// </summary>
    private void ApplyInterfaceTuning()
    {
        if (InterfaceAlias.Length == 0) return;
        Netsh($"interface ipv4 set subinterface \"{InterfaceAlias}\" mtu={TunnelMtu} store=active");
        Netsh($"interface ipv4 set interface \"{InterfaceAlias}\" metric=1 store=active");
    }

    private static string ResolveAlias(ulong luid)
    {
        try
        {
            var sb = new StringBuilder(260);
            if (IpHelper.ConvertInterfaceLuidToAlias(in luid, sb, (nuint)sb.Capacity) == IpHelper.NO_ERROR)
                return sb.ToString();
        }
        catch { }
        return AdapterName;
    }

    // ── Data path ────────────────────────────────────────────────────────────

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
                    Failed?.Invoke(Loc.T("Wt_ReadStopped", err));
                break;
            }
        }
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
        _luid      = 0;
    }

    public void Dispose()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _readThread?.Join(1500); } catch { }
        _cts?.Dispose();
        _cts = null;
        Cleanup();
        AssignedIp     = "";
        InterfaceAlias = "";
    }
}
