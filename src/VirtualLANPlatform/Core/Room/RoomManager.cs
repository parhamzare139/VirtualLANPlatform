using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Storage;
using LiteNetLib;

namespace VirtualLANPlatform.Core.Room;

public sealed class RoomManager : IDisposable
{
    private readonly P2PManager     _p2p;
    private readonly DatabaseManager _db;
    private readonly RoomRepository  _repo;

    public string?  RoomId     { get; private set; }
    public bool     IsHost     { get; private set; }
    public bool     IsActive   { get; private set; }
    public string?  MyUsername { get; private set; }

    private readonly ConcurrentDictionary<int, MemberRecord> _members = new();
    private bool _disposed;

    public event Action<string>?         StatusChanged;
    public event Action<MemberRecord>?   MemberJoined;
    public event Action<MemberRecord>?   MemberLeft;
    public event Action<string, string>? ConnectionFailed;
    public event Action<string>?         RoomClosed;
    public event Action<string>?         ScreenShareStarted;
    public event Action<string>?         ScreenShareStopped;
    public event Action<string, byte[]>? ScreenShareFrame;
    public event Action<byte[]>?         ScreenShareAudioReceived;

    public RoomManager(P2PManager p2p, DatabaseManager db)
    {
        _p2p  = p2p;
        _db   = db;
        _repo = new RoomRepository(db);

        _p2p.ValidateGuest    =  ValidateIncoming;
        _p2p.PeerConnected    += OnPeerConnected;
        _p2p.PeerDisconnected += OnPeerDisconnected;
        _p2p.MessageReceived  += OnMessageReceived;
        _p2p.ConnectionFailed += (t, m) => ConnectionFailed?.Invoke(t, m);
    }

    private string? ValidateIncoming(string username, System.Net.IPEndPoint _)
    {
        if (_members.Values.Any(m => m.Username == username))
            return $"نام کاربری «{username}» قبلاً در این Room استفاده می‌شود";
        return null;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string LocalIP, ushort Port)> CreateRoomAsync(
        string username, ushort port = 42777, string? localIp = null, CancellationToken ct = default)
    {
        MyUsername = username;
        IsHost     = true;

        StatusChanged?.Invoke("در حال راه‌اندازی Room...");

        var (ok, resolvedIp, boundPort) = await _p2p.StartAsHostAsync(username, port, localIp, ct);
        if (!ok) return (false, "", 0);

        RoomId   = GenerateRoomId();
        IsActive = true;

        _repo.SaveRoom(RoomId, username, port);

        var hostMember = new MemberRecord(-1, username, DateTime.UtcNow);
        _members[-1] = hostMember;
        _repo.RecordJoin(RoomId, username);

        MemberJoined?.Invoke(hostMember);
        StatusChanged?.Invoke("Room فعال — منتظر اتصال");
        return (true, resolvedIp, boundPort);
    }

    public async Task<bool> JoinRoomAsync(
        string hostIp, ushort hostPort, string username, CancellationToken ct = default)
    {
        MyUsername = username;
        IsHost     = false;

        StatusChanged?.Invoke("در حال اتصال...");

        bool ok = await _p2p.ConnectAsGuestAsync(hostIp, hostPort, username, ct);
        if (!ok) return false;

        IsActive = true;
        return true;
    }

    public IReadOnlyList<MemberRecord> GetMembers() => _members.Values.ToList();

    // ── Screen Share ──────────────────────────────────────────────────────────

    public void BroadcastScreenShareStart()
        => _p2p.SendToAll(MessageType.ScreenShareStart,
               Encoding.UTF8.GetBytes(MyUsername ?? ""), DeliveryMethod.ReliableOrdered);

    public void BroadcastScreenShareFrame(byte[] jpegBytes)
        => _p2p.SendToAll(MessageType.ScreenShareFrame, jpegBytes, DeliveryMethod.ReliableUnordered);

    public void BroadcastScreenShareStop()
        => _p2p.SendToAll(MessageType.ScreenShareStop, [], DeliveryMethod.ReliableOrdered);

    public void BroadcastScreenShareAudio(byte[] packet)
        => _p2p.SendToAll(MessageType.ScreenShareAudio, packet, DeliveryMethod.Unreliable);

    // ── Peer events ───────────────────────────────────────────────────────────

    private void OnPeerConnected(PeerInfo peer)
    {
        var member = new MemberRecord(peer.Id, peer.Username, DateTime.UtcNow);
        _members[peer.Id] = member;

        if (RoomId != null) _repo.RecordJoin(RoomId, peer.Username);

        if (IsHost)
        {
            SendHandshakeResponse(peer.Id);
            BroadcastMemberSync("join", peer.Username);
        }

        MemberJoined?.Invoke(member);
        StatusChanged?.Invoke($"متصل — {_members.Count} عضو");
    }

    private void OnPeerDisconnected(int peerId, string reason)
    {
        if (_members.TryRemove(peerId, out var member))
        {
            if (RoomId != null) _repo.RecordLeave(RoomId, member.Username);
            if (IsHost) BroadcastMemberSync("leave", member.Username);
            MemberLeft?.Invoke(member with { LeftAt = DateTime.UtcNow });
            StatusChanged?.Invoke(_members.Count > 0
                ? $"متصل — {_members.Count} عضو"
                : "قطع شده");
        }
    }

    // ── Message dispatch ──────────────────────────────────────────────────────

    private void OnMessageReceived(int peerId, MessageFrame frame)
    {
        switch (frame.Type)
        {
            case MessageType.Handshake when !IsHost:
                HandleHandshakeResponse(frame.Payload.ToArray());
                break;

            case MessageType.MemberSync when !IsHost:
                HandleMemberSync(frame.Payload.ToArray());
                break;

            case MessageType.Disconnect:
                if (Encoding.UTF8.GetString(frame.Payload.ToArray()) == "host_close" && !IsHost)
                {
                    IsActive = false;
                    RoomClosed?.Invoke("میزبان Room را بست");
                }
                break;

            case MessageType.ScreenShareStart:
                ScreenShareStarted?.Invoke(Encoding.UTF8.GetString(frame.Payload.ToArray()));
                break;

            case MessageType.ScreenShareFrame:
                string sender = _members.TryGetValue(peerId, out var m) ? m.Username : "—";
                ScreenShareFrame?.Invoke(sender, frame.Payload.ToArray());
                break;

            case MessageType.ScreenShareStop:
                string stopper = _members.TryGetValue(peerId, out var ms) ? ms.Username : "—";
                ScreenShareStopped?.Invoke(stopper);
                break;

            case MessageType.ScreenShareAudio:
                ScreenShareAudioReceived?.Invoke(frame.Payload.ToArray());
                break;
        }
    }

    private void HandleHandshakeResponse(byte[] data)
    {
        var hs = HandshakePayload.Deserialize(data);
        if (hs == null) return;

        RoomId = hs.RoomId;
        _members.Clear();
        foreach (var dto in hs.Members)
        {
            var record = new MemberRecord(-2, dto.Username, DateTime.UtcNow);
            _members[dto.Username.GetHashCode()] = record;
            MemberJoined?.Invoke(record);
        }

        StatusChanged?.Invoke($"عضو شدید — {_members.Count} عضو در Room");
    }

    private void HandleMemberSync(byte[] data)
    {
        var sync = MemberSyncPayload.Deserialize(data);
        if (sync == null) return;

        _members.Clear();
        foreach (var dto in sync.Members)
            _members[dto.Username.GetHashCode()] =
                new MemberRecord(-2, dto.Username, DateTime.UtcNow);

        var evtMember = new MemberRecord(-2, sync.Username, DateTime.UtcNow);
        if (sync.Event == "join") MemberJoined?.Invoke(evtMember);
        else                      MemberLeft?.Invoke(evtMember);

        StatusChanged?.Invoke($"متصل — {_members.Count} عضو");
    }

    private void SendHandshakeResponse(int peerId)
    {
        var hs = new HandshakePayload
        {
            RoomId  = RoomId ?? "",
            Members = _members.Values
                .Select(m => new MemberDto { Username = m.Username })
                .ToArray()
        };
        _p2p.SendToPeer(peerId, MessageType.Handshake, hs.Serialize(),
            DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastMemberSync(string evt, string username)
    {
        var sync = new MemberSyncPayload
        {
            Event    = evt,
            Username = username,
            Members  = _members.Values
                .Select(m => new MemberDto { Username = m.Username })
                .ToArray()
        };
        _p2p.SendToAll(MessageType.MemberSync, sync.Serialize(), DeliveryMethod.ReliableOrdered);
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public async Task CloseRoomAsync()
    {
        if (!IsActive) return;
        _p2p.SendToAll(MessageType.Disconnect,
            Encoding.UTF8.GetBytes("host_close"), DeliveryMethod.ReliableOrdered);
        await Task.Delay(150);
        Shutdown();
    }

    public async Task LeaveRoomAsync()
    {
        if (!IsActive) return;
        _p2p.SendToAll(MessageType.Disconnect,
            Encoding.UTF8.GetBytes("guest_leave"), DeliveryMethod.ReliableOrdered);
        await Task.Delay(50);
        Shutdown();
    }

    public void Shutdown()
    {
        IsActive = false;
        _p2p.Shutdown();
        _members.Clear();
    }

    private static string GenerateRoomId()
        => Guid.NewGuid().ToString("N")[..8].ToUpper();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _p2p.PeerConnected    -= OnPeerConnected;
        _p2p.PeerDisconnected -= OnPeerDisconnected;
        _p2p.MessageReceived  -= OnMessageReceived;
        Shutdown();
        _p2p.Dispose();
        _db.Dispose();
    }
}
