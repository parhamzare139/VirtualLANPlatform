using System.Collections.Concurrent;
using System.Net;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using LiteNetLib;

namespace VirtualLANPlatform.Core.VirtualNetwork;

/// <summary>
/// Bridges the WinTun virtual adapter and the P2PManager.
///
/// Phase 9 routing model:
///   TUN → outbound packets from OS:
///     Host  — routes by destination IP (unicast → specific peer, broadcast → all peers)
///     Guest — sends everything to Host (Host handles all forwarding)
///
///   P2P → inbound packets from peers:
///     Host  — injects into own TUN if destined for Host or broadcast,
///              then forwards to the correct guest peer
///     Guest — injects directly into TUN (Host already routed correctly)
/// </summary>
public sealed class VirtualNetworkManager : IDisposable
{
    private const string HostVIP     = "10.77.0.1";
    private const string SubnetBcast = "10.77.255.255";

    private readonly VirtualAdapter   _adapter = new();
    private readonly VirtualIPManager _ipMgr   = new();
    private P2PManager? _p2p;
    private bool _isHost;

    // Routing table — updated by RoomManager as guests connect/disconnect
    private readonly ConcurrentDictionary<string, int> _ipToPeer  = new(); // vip  → peerId
    private readonly ConcurrentDictionary<int, string> _peerToIp  = new(); // peerId → vip

    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public IPAddress? VirtualIP => _ipMgr.AssignedIP;
    public bool       IsActive  { get; private set; }

    public event Action<string>? StatusChanged;

    // ── Routing table ─────────────────────────────────────────────────────────

    public void AddRoute(int peerId, string virtualIP)
    {
        _ipToPeer[virtualIP] = peerId;
        _peerToIp[peerId]    = virtualIP;
    }

    public void RemoveRoute(int peerId)
    {
        if (_peerToIp.TryRemove(peerId, out string? ip))
            _ipToPeer.TryRemove(ip, out _);
    }

    // ── Start ─────────────────────────────────────────────────────────────────

    public async Task<IPAddress> StartAsync(P2PManager p2p, bool isHost,
        byte guestOctet = 2, CancellationToken ct = default)
    {
        _p2p    = p2p;
        _isHost = isHost;

        StatusChanged?.Invoke("در حال ایجاد آداپتور مجازی...");
        _adapter.Open();
        _adapter.StartSession();

        uint v = VirtualAdapter.GetDriverVersion();
        StatusChanged?.Invoke($"WinTun {v >> 16}.{v & 0xFFFF} بارگذاری شد");

        StatusChanged?.Invoke("در حال اختصاص آدرس IP مجازی...");
        IPAddress vip = _ipMgr.AssignIP(_adapter.Luid, isHost, guestOctet);
        StatusChanged?.Invoke($"IP مجازی: {vip}  (subnet: {VirtualIPManager.SubnetDescription})");

        _p2p.MessageReceived += OnP2PMessageReceived;

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => TunReadLoopAsync(_readCts.Token), _readCts.Token);

        IsActive = true;
        await Task.CompletedTask;
        return vip;
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;

        _readCts?.Cancel();
        if (_p2p != null) _p2p.MessageReceived -= OnP2PMessageReceived;

        _ipMgr.Release();
        _adapter.Dispose();
        _ipToPeer.Clear();
        _peerToIp.Clear();
        StatusChanged?.Invoke("آداپتور مجازی متوقف شد");
    }

    // ── TUN → P2P (outbound from OS) ─────────────────────────────────────────

    private async Task TunReadLoopAsync(CancellationToken ct)
    {
        nint waitEvent = _adapter.GetReadWaitEvent();
        while (!ct.IsCancellationRequested)
        {
            uint wr = WinTunNative.WaitForSingleObject(waitEvent, 100);
            if (ct.IsCancellationRequested) break;
            if (wr == WinTunNative.WAIT_TIMEOUT) continue;
            DrainRing(ct);
            await Task.Yield();
        }
    }

    private unsafe void DrainRing(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_adapter.TryReceivePacket(out ReadOnlySpan<byte> pkt, out byte* raw))
                break;
            try   { ForwardTunPacketToPeers(pkt); }
            finally { _adapter.ReleaseReceivePacket(raw); }
        }
    }

    private void ForwardTunPacketToPeers(ReadOnlySpan<byte> ipPacket)
    {
        if (_p2p == null || !_p2p.IsRunning) return;
        if (ipPacket.Length < 20) return;

        byte[] payload = ipPacket.ToArray();

        if (!_isHost)
        {
            // Guest: Host is our only peer and handles all routing
            _p2p.SendToAll(MessageType.VirtualLanPacket, payload, DeliveryMethod.Unreliable);
            return;
        }

        // Host: route by destination IP (bytes 16–19 in IPv4 header)
        string destIp = $"{ipPacket[16]}.{ipPacket[17]}.{ipPacket[18]}.{ipPacket[19]}";

        if (IsBroadcast(destIp, ipPacket[19]))
        {
            _p2p.SendToAll(MessageType.VirtualLanPacket, payload, DeliveryMethod.Unreliable);
        }
        else if (_ipToPeer.TryGetValue(destIp, out int peerId))
        {
            _p2p.SendToPeer(peerId, MessageType.VirtualLanPacket, payload, DeliveryMethod.Unreliable);
        }
        // unknown destination → drop
    }

    // ── P2P → TUN (inbound from peers) ───────────────────────────────────────

    private void OnP2PMessageReceived(int peerId, MessageFrame frame)
    {
        if (frame.Type != MessageType.VirtualLanPacket) return;
        if (!IsActive) return;

        if (!_isHost)
        {
            // Guest: Host already routed correctly — inject directly
            _adapter.SendPacket(frame.Payload.Span);
            return;
        }

        // Host: parse destination and route
        ReadOnlySpan<byte> packet = frame.Payload.Span;
        if (packet.Length < 20) return;

        string destIp   = $"{packet[16]}.{packet[17]}.{packet[18]}.{packet[19]}";
        bool broadcast  = IsBroadcast(destIp, packet[19]);
        bool forHost    = destIp == HostVIP;

        // Deliver to Host's own network stack if addressed to us or broadcast
        if (forHost || broadcast)
            _adapter.SendPacket(packet);

        if (broadcast)
        {
            // Forward broadcast to all other guests
            byte[] data = frame.Payload.ToArray();
            foreach (var (_, targetId) in _ipToPeer)
                if (targetId != peerId)
                    _p2p.SendToPeer(targetId, MessageType.VirtualLanPacket, data, DeliveryMethod.Unreliable);
        }
        else if (!forHost && _ipToPeer.TryGetValue(destIp, out int targetId))
        {
            // Forward unicast to the correct guest
            _p2p.SendToPeer(targetId, MessageType.VirtualLanPacket,
                frame.Payload.ToArray(), DeliveryMethod.Unreliable);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsBroadcast(string destIp, byte lastOctet)
        => lastOctet == 255 || destIp == "255.255.255.255" || destIp == SubnetBcast;

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _ipMgr.Dispose();
        _readCts?.Dispose();
    }
}
