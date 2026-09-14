using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Storage;
using LiteNetLib;

using VirtualLANPlatform.UI.Localization;

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

    /// <summary>Raised on a guest when the host issues a moderation command against it.</summary>
    public event Action<string, bool>?   ModerationReceived;

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
            return Loc.T("Rm_NameTaken", username);
        return null;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string LocalIP, ushort Port)> CreateRoomAsync(
        string username, ushort port = 42777, string? localIp = null, CancellationToken ct = default)
    {
        MyUsername = username;
        IsHost     = true;

        StatusChanged?.Invoke(Loc.T("Rm_Starting"));

        var (ok, resolvedIp, boundPort) = await _p2p.StartAsHostAsync(username, port, localIp, ct);
        if (!ok) return (false, "", 0);

        RoomId   = GenerateRoomId();
        IsActive = true;

        _repo.SaveRoom(RoomId, username, port);

        var hostMember = new MemberRecord(-1, username, DateTime.UtcNow);
        _members[-1] = hostMember;
        _repo.RecordJoin(RoomId, username);

        MemberJoined?.Invoke(hostMember);
        StatusChanged?.Invoke(Loc.T("Rm_Waiting"));
        return (true, resolvedIp, boundPort);
    }

    public async Task<bool> JoinRoomAsync(
        string hostIp, ushort hostPort, string username, CancellationToken ct = default)
    {
        MyUsername = username;
        IsHost     = false;

        StatusChanged?.Invoke(Loc.T("Rm_Connecting"));

        bool ok = await _p2p.ConnectAsGuestAsync(hostIp, hostPort, username, ct);
        if (!ok) return false;

        IsActive = true;
        return true;
    }

    public IReadOnlyList<MemberRecord> GetMembers() => _members.Values.ToList();

    // ── Moderation (host only) ────────────────────────────────────────────────

    /// <summary>True when <paramref name="username"/> maps to a real, addressable peer.</summary>
    public bool TryGetPeerId(string username, out int peerId)
    {
        foreach (var m in _members.Values)
            if (m.Username == username && m.PeerId >= 0)
            {
                peerId = m.PeerId;
                return true;
            }

        peerId = -1;
        return false;
    }

    /// <summary>Mutes or unmutes a guest's microphone remotely.</summary>
    public bool MuteMember(string username, bool muted)
        => SendModeration(username, "mute", muted);

    /// <summary>Forces a guest to stop sharing their screen.</summary>
    public bool StopMemberShare(string username)
        => SendModeration(username, "stopshare", true);

    /// <summary>Tells a guest it was removed, then drops the connection.</summary>
    public bool KickMember(string username)
    {
        if (!IsHost || !TryGetPeerId(username, out int peerId)) return false;

        SendModeration(username, "kick", true);

        // Give the notice a moment to land before tearing the socket down.
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            _p2p.DisconnectPeer(peerId);
        });
        return true;
    }

    private bool SendModeration(string username, string op, bool on)
    {
        if (!IsHost || !TryGetPeerId(username, out int peerId)) return false;

        _p2p.SendToPeer(peerId, MessageType.Moderation,
            new ModerationPayload { Op = op, On = on }.Serialize(),
            DeliveryMethod.ReliableOrdered);
        return true;
    }

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
        StatusChanged?.Invoke(Loc.T("Rm_MembersN", _members.Count));
    }

    private void OnPeerDisconnected(int peerId, string reason)
    {
        if (_members.TryRemove(peerId, out var member))
        {
            if (RoomId != null) _repo.RecordLeave(RoomId, member.Username);
            if (IsHost) BroadcastMemberSync("leave", member.Username);
            MemberLeft?.Invoke(member with { LeftAt = DateTime.UtcNow });
            StatusChanged?.Invoke(_members.Count > 0
                ? Loc.T("Rm_MembersN", _members.Count)
                : Loc.T("Rm_Disconnected"));
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
                    RoomClosed?.Invoke(Loc.T("Rm_HostClosed"));
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

            case MessageType.Moderation when !IsHost:
            {
                var cmd = ModerationPayload.Deserialize(frame.Payload.ToArray());
                if (cmd is { Op.Length: > 0 })
                    ModerationReceived?.Invoke(cmd.Op, cmd.On);
                break;
            }

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

        StatusChanged?.Invoke(Loc.T("Rm_JoinedN", _members.Count));
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

        StatusChanged?.Invoke(Loc.T("Rm_MembersN", _members.Count));
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
