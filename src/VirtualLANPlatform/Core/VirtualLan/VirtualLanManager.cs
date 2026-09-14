using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteNetLib;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;

using VirtualLANPlatform.UI.Localization;

namespace VirtualLANPlatform.Core.VirtualLan;

/// <summary>
/// A standalone virtual LAN — independent of chat rooms. One machine hosts, others
/// join by its real IP; every participant gets a <c>10.88.0.x</c> address on a Wintun
/// adapter and their OS traffic to those addresses is tunnelled over an encrypted
/// P2P link (its own transport on <see cref="VlanPort"/>, separate from the room port).
///
/// Topology is star + host relay: guests send everything to the host, which routes by
/// destination IP (and floods broadcast/multicast to all).
/// </summary>
public sealed class VirtualLanManager : IDisposable
{
    public const ushort VlanPort = 42778;

    private const string Mask         = "255.255.0.0";
    private const int    PrefixLength = 16;
    private const string Subnet       = "10.88.0.0/16";
    private const uint   NetBase      = 0x0A58_0000; // 10.88.0.0
    private const uint   NetBcast     = 0x0A58_FFFF; // 10.88.255.255

    /// <summary>
    /// Largest packet we will hand to an unreliable channel. LiteNetLib cannot fragment
    /// unreliable sends and <i>throws</i> when one exceeds the link MTU, so anything above
    /// this goes reliable instead. The tunnel MTU is 1280, but the OS is free to hand us a
    /// bigger frame regardless — a path-MTU probe, or an app that sets DF and ignores the
    /// interface MTU — and one of those must not take the tunnel down.
    /// </summary>
    private const int MaxUnreliableSize = 1100;

    /// <summary>
    /// Picks the channel for one tunnelled packet. Unreliable by default: these are whole
    /// IP packets, whatever runs inside does its own retransmission, and a reliable channel
    /// underneath adds head-of-line blocking to exactly the traffic — game state, voice —
    /// that would rather drop a datagram than wait for it.
    /// </summary>
    private static DeliveryMethod DeliveryFor(byte[] packet)
        => packet.Length <= MaxUnreliableSize
            ? DeliveryMethod.Unreliable
            : DeliveryMethod.ReliableUnordered;

    private readonly P2PManager    _p2p    = new();
    private readonly WintunSession _tun    = new();
    private readonly PortMapper    _mapper = new();

    /// <summary>This machine's own virtual IP — derived from its physical hardware
    /// identity so two different people hosting never end up with the same address
    /// (see <see cref="ComputeMyHostPart"/>).</summary>
    private readonly uint _myVip = NetBase | ComputeMyHostPart();

    // Written from the UI-triggered lifecycle methods, read from the tunnel-read
    // thread (OnLocalPacket) and the VLAN P2P poll thread (OnP2PMessage) — volatile
    // so a state change becomes visible to those threads promptly, not just eventually.
    private volatile bool _isHost;
    private volatile bool _active;
    private bool _disposed;

    // Host-only: virtual-IP bookkeeping.
    private readonly ConcurrentDictionary<int, uint>  _peerToVip = new();
    private readonly ConcurrentDictionary<uint, int>  _vipToPeer = new();
    private readonly object _allocLock = new();
    private uint _nextHostByte = 2;

    // Guest-only: completes when the host grants us an IP.
    private TaskCompletionSource<string>? _assignTcs;

    public bool   IsActive   => _active;
    public bool   IsHost     => _isHost;
    public string AssignedIp { get; private set; } = "";

    /// <summary>
    /// The address guests outside this LAN must dial, once the router has been asked to
    /// forward our port. Null when UPnP was unavailable — hosting then only reaches
    /// machines on the same physical network.
    /// </summary>
    public string? PublicEndpoint { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<string>? Connected;     // arg = assigned IP
    public event Action?         Disconnected;
    public event Action<string>? Error;

    public VirtualLanManager()
    {
        _p2p.PeerConnected    += OnPeerConnected;
        _p2p.PeerDisconnected += OnPeerDisconnected;
        _p2p.MessageReceived  += OnP2PMessage;
        _p2p.ConnectionFailed += (t, m) => Error?.Invoke($"{t}: {m}");
        _tun.PacketReceived   += OnLocalPacket;
        // Failed fires on the tunnel read thread; DisconnectAsync joins that same
        // thread, so it must run elsewhere.
        _tun.Failed           += m => { Error?.Invoke(m); _ = Task.Run(() => DisconnectAsync()); };
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public async Task<bool> HostAsync(string username, string? localIp = null, CancellationToken ct = default)
    {
        if (_active) return false;
        _isHost = true;

        StatusChanged?.Invoke(Loc.T("Vm_Starting"));
        var (ok, _, _) = await _p2p.StartAsHostAsync(username, VlanPort, localIp, ct);
        if (!ok) { _isHost = false; Error?.Invoke(Loc.T("Vm_PortBusy", VlanPort)); return false; }

        // _tun.Start() configures the interface and shells out to netsh — keep it off
        // the caller's thread.
        if (!await Task.Run(() => _tun.Start(Ip(_myVip), PrefixLength), ct)) { await DisconnectAsync(); return false; }
        await Task.Run(() => OpenSubnetFirewall(), ct);

        _active    = true;
        AssignedIp = Ip(_myVip);

        // Ask the router to forward our port. Guests elsewhere on the internet cannot
        // reach us otherwise: their first packet hits the router with no NAT entry and
        // is dropped before we ever hear about it. Best effort — plenty of networks have
        // UPnP off, and behind carrier-grade NAT no port can be opened at all, which
        // leaves hosting working for the local network only.
        StatusChanged?.Invoke(Loc.T("Vm_OpeningPort"));
        var map = await _mapper.MapAsync(VlanPort, "VirtualLAN Platform", ct);
        PublicEndpoint = map.Ok ? map.ExternalIp : null;

        StatusChanged?.Invoke(map.Ok
            ? Loc.T("Vm_LiveWaiting")
            : Loc.T("Vm_LiveLocal"));
        Connected?.Invoke(AssignedIp);
        return true;
    }

    public async Task<bool> JoinAsync(string hostRealIp, string username, CancellationToken ct = default)
    {
        if (_active) return false;
        _isHost = false;
        _assignTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        StatusChanged?.Invoke(Loc.T("Vm_Joining"));
        if (!await _p2p.ConnectAsGuestAsync(hostRealIp, VlanPort, username, ct))
        {
            Error?.Invoke(Loc.T("Vm_JoinFailed"));
            _p2p.Shutdown();
            return false;
        }

        string ip;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            ip = await _assignTcs.Task.WaitAsync(timeout.Token);
        }
        catch
        {
            Error?.Invoke(Loc.T("Vm_NoIpGranted"));
            _p2p.Shutdown();
            return false;
        }

        if (!await Task.Run(() => _tun.Start(ip, PrefixLength), ct)) { await DisconnectAsync(); return false; }
        await Task.Run(() => OpenSubnetFirewall(), ct);

        _active    = true;
        AssignedIp = ip;
        StatusChanged?.Invoke(Loc.T("Vlan_ConnectedStatus", ip));
        Connected?.Invoke(ip);
        return true;
    }

    public Task DisconnectAsync()
    {
        if (_disposed) return Task.CompletedTask;
        bool was = _active;
        _active = false;

        try { if (was) _p2p.SendToAll(MessageType.Disconnect, Encoding.UTF8.GetBytes("vlan_bye"), DeliveryMethod.ReliableOrdered); } catch { }

        _tun.Dispose();
        _p2p.Shutdown();
        // Leave the router as we found it rather than accumulating a stale forward.
        try { _mapper.UnmapAsync().GetAwaiter().GetResult(); } catch { }
        PublicEndpoint = null;
        _peerToVip.Clear();
        _vipToPeer.Clear();
        lock (_allocLock) _nextHostByte = 2;
        AssignedIp = "";

        if (was)
        {
            StatusChanged?.Invoke(Loc.T("Vm_Stopped"));
            Disconnected?.Invoke();
        }
        return Task.CompletedTask;
    }

    // ── Peer events (host assigns / frees virtual IPs) ───────────────────────

    private void OnPeerConnected(PeerInfo peer)
    {
        if (!_isHost) return;

        uint vip = AllocateVip();
        _peerToVip[peer.Id] = vip;
        _vipToPeer[vip]     = peer.Id;

        var dto = new VlanControlDto { Op = "assign", Ip = Ip(vip), Mask = Mask };
        _p2p.SendToPeer(peer.Id, MessageType.VirtualLanControl,
            JsonSerializer.SerializeToUtf8Bytes(dto), DeliveryMethod.ReliableOrdered);

        StatusChanged?.Invoke(Loc.T("Vm_NewMember", Ip(vip)));
    }

    private void OnPeerDisconnected(int peerId, string reason)
    {
        if (_isHost)
        {
            if (_peerToVip.TryRemove(peerId, out uint vip))
            {
                _vipToPeer.TryRemove(vip, out _);
                FreeVip(vip);
            }
            return;
        }

        // Guest lost the host — the virtual LAN is over. Hop off the P2P poll
        // thread: DisconnectAsync() shuts that same transport down.
        if (_active) _ = Task.Run(() => DisconnectAsync());
        else _assignTcs?.TrySetException(new Exception("host gone"));
    }

    // ── Data path ────────────────────────────────────────────────────────────

    /// <summary>An IP packet the local OS wants to send out our virtual adapter.</summary>
    private void OnLocalPacket(byte[] pkt)
    {
        if (!_active) return;
        uint dst = DestIp(pkt);
        if (dst == 0) return;

        if (_isHost)
        {
            if (IsFlood(dst)) { _p2p.SendToAll(MessageType.VirtualLanPacket, pkt, DeliveryFor(pkt)); return; }
            if (_vipToPeer.TryGetValue(dst, out int peerId))
                _p2p.SendToPeer(peerId, MessageType.VirtualLanPacket, pkt, DeliveryFor(pkt));
            // else: not a participant — drop (we don't tunnel off-subnet traffic)
        }
        else
        {
            // Guest: host is our only peer and does the routing.
            _p2p.SendToAll(MessageType.VirtualLanPacket, pkt, DeliveryFor(pkt));
        }
    }

    private void OnP2PMessage(int peerId, MessageFrame frame)
    {
        switch (frame.Type)
        {
            case MessageType.VirtualLanPacket:
            {
                byte[] pkt = frame.Payload.ToArray();
                uint dst = DestIp(pkt);

                if (!_isHost) { _tun.WritePacket(pkt, pkt.Length); break; }

                // Host routing.
                if (IsFlood(dst))
                {
                    _tun.WritePacket(pkt, pkt.Length);
                    foreach (var other in _vipToPeer.Values)
                        if (other != peerId)
                            _p2p.SendToPeer(other, MessageType.VirtualLanPacket, pkt, DeliveryFor(pkt));
                }
                else if (dst == _myVip)
                {
                    _tun.WritePacket(pkt, pkt.Length);
                }
                else if (_vipToPeer.TryGetValue(dst, out int target) && target != peerId)
                {
                    _p2p.SendToPeer(target, MessageType.VirtualLanPacket, pkt, DeliveryFor(pkt));
                }
                break;
            }

            case MessageType.VirtualLanControl when !_isHost:
            {
                var dto = TryParse(frame.Payload.ToArray());
                if (dto is { Op: "assign", Ip.Length: > 0 })
                    _assignTcs?.TrySetResult(dto.Ip);
                break;
            }

            case MessageType.Disconnect:
            {
                if (Encoding.UTF8.GetString(frame.Payload.ToArray()) == "vlan_bye" && !_isHost && _active)
                    _ = Task.Run(() => DisconnectAsync());   // off the poll thread
                break;
            }
        }
    }

    // ── Firewall ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Allows all traffic to and from the virtual subnet through Windows Firewall.
    ///
    /// A freshly created adapter lands in the Public network profile, where the firewall
    /// drops unsolicited inbound traffic. That is invisible from inside the app — the
    /// tunnel is up, peers are connected, and every ping and game-discovery broadcast is
    /// dropped by the receiver before anything sees it, which reads as "the virtual LAN
    /// does not work". Scoping the rules to 10.88.0.0/16 keeps this narrow: nothing is
    /// opened on the real network.
    /// </summary>
    private static void OpenSubnetFirewall()
    {
        const string ruleName = "VirtualLANPlatform VLAN Subnet";
        // Delete first so repeated runs replace rather than stack duplicates.
        Netsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        Netsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in  action=allow protocol=any remoteip={Subnet}");
        Netsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=allow protocol=any remoteip={Subnet}");
    }

    private static int Netsh(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", args)
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private uint AllocateVip()
    {
        lock (_allocLock)
        {
            while (_nextHostByte < 0xFFFE)
            {
                uint candidate = NetBase | (_nextHostByte & 0xFFFF);
                _nextHostByte++;
                if (candidate != _myVip && !_vipToPeer.ContainsKey(candidate))
                    return candidate;
            }
            // Pool exhausted (65k peers) — extremely unlikely; wrap and reuse gaps.
            for (uint h = 2; h < 0xFFFE; h++)
            {
                uint candidate = NetBase | h;
                if (candidate != _myVip && !_vipToPeer.ContainsKey(candidate)) return candidate;
            }
            return NetBase | 2;
        }
    }

    private void FreeVip(uint vip)
    {
        lock (_allocLock)
        {
            uint host = vip & 0xFFFF;
            if (host == _nextHostByte - 1) _nextHostByte--; // cheap reclaim of the top of the range
        }
    }

    /// <summary>Destination address from an IPv4 header (bytes 16..19), big-endian → uint. 0 if not IPv4.</summary>
    private static uint DestIp(byte[] pkt)
    {
        if (pkt.Length < 20 || (pkt[0] >> 4) != 4) return 0;
        return (uint)(pkt[16] << 24 | pkt[17] << 16 | pkt[18] << 8 | pkt[19]);
    }

    private static bool IsFlood(uint ip)
        => ip == 0xFFFF_FFFF || ip == NetBcast || (ip >> 28) == 0xE; // 255.255.255.255 | 10.88.255.255 | 224.0.0.0/4

    private static string Ip(uint v)
        => $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    // Our own tunnel adapter's interface name — excluded when scanning physical
    // hardware below, in case a stale one from a previous run is still present.
    private const string AdapterHint = "VirtualLAN";

    /// <summary>
    /// A 16-bit value tied to this machine's physical network hardware (every up,
    /// physical adapter's MAC address, sorted for determinism and hashed), so the
    /// same computer gets the same virtual IP every time it hosts, and two different
    /// computers get different ones with overwhelming probability — no coordination
    /// with anyone else is needed or possible. Falls back to a locally persisted
    /// random value on machines with no usable physical adapter (unusual VMs).
    /// </summary>
    private static ushort ComputeMyHostPart()
    {
        try
        {
            byte[] macBytes = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                         && n.Name != AdapterHint)
                .Select(n => n.GetPhysicalAddress().GetAddressBytes())
                .Where(b => b.Length == 6 && Array.Exists(b, x => x != 0))
                .OrderBy(b => Convert.ToHexString(b), StringComparer.Ordinal)
                .SelectMany(b => b)
                .ToArray();

            if (macBytes.Length > 0) return HashToHostPart(macBytes);
        }
        catch { }

        return PersistedFallbackHostPart();
    }

    private static ushort HashToHostPart(byte[] seed)
    {
        byte[] hash = SHA256.HashData(seed);
        ushort h = (ushort)(hash[0] << 8 | hash[1]);
        if (h == 0x0000) h = 1;      // reserve .0.0 (network address)
        if (h == 0xFFFF) h = 0xFFFE; // reserve .255.255 (broadcast)
        return h;
    }

    /// <summary>Used only when the machine has no physical MAC we can hash (e.g. some
    /// sandboxes/VMs) — a random value generated once and reused on every future run.</summary>
    private static ushort PersistedFallbackHostPart()
    {
        string path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VirtualLANPlatform", "vlan_id.txt");
        try
        {
            if (System.IO.File.Exists(path) &&
                ushort.TryParse(System.IO.File.ReadAllText(path).Trim(), out ushort saved) &&
                saved is not (0x0000 or 0xFFFF))
                return saved;
        }
        catch { }

        ushort fresh = (ushort)Random.Shared.Next(1, 0xFFFF);
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (dir != null) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path, fresh.ToString());
        }
        catch { }
        return fresh;
    }

    private static VlanControlDto? TryParse(byte[] data)
    {
        try { return JsonSerializer.Deserialize<VlanControlDto>(data); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Disconnect BEFORE flagging disposed, and wait for it. The old order set the
        // flag first and fired DisconnectAsync without awaiting — and DisconnectAsync
        // returns immediately when that flag is set, so closing the app tore nothing
        // down: peers were never told, the router mapping was left in place, and the
        // status never reached the UI.
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }

        _disposed = true;
        _p2p.PeerConnected    -= OnPeerConnected;
        _p2p.PeerDisconnected -= OnPeerDisconnected;
        _p2p.MessageReceived  -= OnP2PMessage;
        _tun.Dispose();
        _p2p.Dispose();
        _mapper.Dispose();
    }

    private sealed class VlanControlDto
    {
        [JsonPropertyName("op")]   public string  Op   { get; set; } = "";
        [JsonPropertyName("ip")]   public string  Ip   { get; set; } = "";
        [JsonPropertyName("mask")] public string  Mask { get; set; } = "";
    }
}
