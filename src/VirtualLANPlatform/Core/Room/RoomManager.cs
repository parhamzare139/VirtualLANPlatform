using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Storage;
using VirtualLANPlatform.Core.VirtualNetwork;
using LiteNetLib;

namespace VirtualLANPlatform.Core.Room;

/// <summary>
/// Orchestrates the full Room lifecycle:
///   Host: create room → listen → assign VIPs → sync members
///   Guest: join room → receive VIP → start virtual network
///
/// Sits above P2PManager and VirtualNetworkManager.
/// </summary>
public sealed class RoomManager : IDisposable
{
    public const string  HostVIP    = "10.77.0.1";
    private const string SubnetMask = "255.255.0.0";

    private static readonly string AssignmentsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualLANPlatform", "ip_assignments.json");

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly P2PManager           _p2p;
    private readonly VirtualNetworkManager _vnet;
    private readonly DatabaseManager      _db;
    private readonly RoomRepository       _repo;

    // ── Room state ────────────────────────────────────────────────────────────

    public string?  RoomId    { get; private set; }
    public bool     IsHost    { get; private set; }
    public bool     IsActive  { get; private set; }
    public string?  MyVIP     { get; private set; }
    public string?  MyUsername { get; private set; }

    private readonly ConcurrentDictionary<int, MemberRecord> _members = new();
    private int _nextGuestOctet = 2; // 10.77.0.2, 10.77.0.3, …

    // Persists username→octet mapping so returning guests keep the same virtual IP
    private readonly Dictionary<string, byte> _assignments;

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<string>?       StatusChanged;
    public event Action<MemberRecord>? MemberJoined;
    public event Action<MemberRecord>? MemberLeft;
    public event Action<string, string>? ConnectionFailed;
    public event Action<string>?       RoomClosed;   // fires on Guest when Host closes room
    public event Action<string>?       VipAssigned;  // fires when MyVIP is ready (Host + Guest)

    private bool _disposed;

    // ── Constructor ───────────────────────────────────────────────────────────

    public RoomManager(P2PManager p2p, VirtualNetworkManager vnet, DatabaseManager db)
    {
        _p2p  = p2p;
        _vnet = vnet;
        _db   = db;
        _repo = new RoomRepository(db);

        _assignments = LoadAssignments();

        _p2p.ValidateGuest    =  ValidateIncoming;
        _p2p.PeerConnected    += OnPeerConnected;
        _p2p.PeerDisconnected += OnPeerDisconnected;
        _p2p.MessageReceived  += OnMessageReceived;
        _p2p.ConnectionFailed += (t, m) => ConnectionFailed?.Invoke(t, m);

        _vnet.StatusChanged += msg => StatusChanged?.Invoke(msg);
    }

    // Returns null to accept, error string to reject
    private string? ValidateIncoming(string username, System.Net.IPEndPoint endpoint)
    {
        if (_members.Values.Any(m => m.Username == username))
            return $"نام کاربری «{username}» قبلاً در این Room استفاده می‌شود — لطفاً نام دیگری انتخاب کنید";
        return null;
    }

    private static Dictionary<string, byte> LoadAssignments()
    {
        try
        {
            if (File.Exists(AssignmentsPath))
                return JsonSerializer.Deserialize<Dictionary<string, byte>>(
                    File.ReadAllText(AssignmentsPath)) ?? [];
        }
        catch { }
        return [];
    }

    private void SaveAssignments()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AssignmentsPath)!);
            File.WriteAllText(AssignmentsPath, JsonSerializer.Serialize(_assignments));
        }
        catch { }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Creates a Room as Host. Returns the Connection Code.</summary>
    public async Task<(bool Ok, string Code)> CreateRoomAsync(
        string username, ushort port = 42777, CancellationToken ct = default)
    {
        MyUsername = username;
        IsHost     = true;

        StatusChanged?.Invoke("در حال راه‌اندازی Room...");

        var (ok, code) = await _p2p.StartAsHostAsync(username, port, ct);
        if (!ok) return (false, "");

        MyVIP = HostVIP; // Host IP is always fixed — no need to wait for VNet
        VipAssigned?.Invoke(HostVIP);

        // Start virtual network in background — same pattern as Guest; errors go to status log
        _ = Task.Run(async () =>
        {
            try
            {
                await _vnet.StartAsync(_p2p, isHost: true, ct: ct);
                StatusChanged?.Invoke($"VNet فعال — IP: {HostVIP}");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"خطا در VNet: {ex.Message}");
            }
        }, ct);

        // Generate Room ID and persist
        RoomId = GenerateRoomId();
        _repo.SaveRoom(RoomId, username, port);

        // Add self to member list (Host entry, peerId = -1)
        var hostMember = new MemberRecord(-1, username, HostVIP, DateTime.UtcNow);
        _members[-1] = hostMember;
        _repo.RecordJoin(RoomId, hostMember);

        IsActive = true;
        StatusChanged?.Invoke($"Room فعال — کد: {code}");
        return (true, code);
    }

    /// <summary>Joins an existing Room as Guest.</summary>
    public async Task<bool> JoinRoomAsync(
        string code, string username, CancellationToken ct = default)
    {
        MyUsername = username;
        IsHost     = false;

        StatusChanged?.Invoke("در حال اتصال...");

        bool ok = await _p2p.ConnectAsGuestAsync(code, username, ct);
        if (!ok) return false;

        // VirtualNetwork is started after receiving HandshakeResponse from Host
        IsActive = true;
        return true;
    }

    /// <summary>Returns a snapshot of the current member list.</summary>
    public IReadOnlyList<MemberRecord> GetMembers()
        => _members.Values.ToList();

    // ── Host: peer connected ──────────────────────────────────────────────────

    private void OnPeerConnected(PeerInfo peer)
    {
        if (!IsHost) return;

        // Reuse saved octet for returning guest; skip occupied slots; save new assignments
        byte octet;
        if (_assignments.TryGetValue(peer.Username, out byte saved) &&
            !_members.Values.Any(m => m.VirtualIP == $"10.77.0.{saved}"))
        {
            octet = saved;
            if (octet >= _nextGuestOctet) _nextGuestOctet = octet + 1;
        }
        else
        {
            octet = (byte)_nextGuestOctet++;
            while (_members.Values.Any(m => m.VirtualIP == $"10.77.0.{octet}"))
                octet = (byte)_nextGuestOctet++;
        }
        _assignments[peer.Username] = octet;
        SaveAssignments();

        string guestVIP = $"10.77.0.{octet}";

        var member = new MemberRecord(peer.Id, peer.Username, guestVIP, DateTime.UtcNow);
        _members[peer.Id] = member;

        if (RoomId != null) _repo.RecordJoin(RoomId, member);

        // Register route so VirtualNetworkManager can forward packets to this guest
        _vnet.AddRoute(peer.Id, guestVIP);

        // Send Handshake response to the new guest
        SendHandshakeResponse(peer.Id, guestVIP);

        // Broadcast updated member list to all (including new peer)
        BroadcastMemberSync("join", peer.Username);

        MemberJoined?.Invoke(member);
        StatusChanged?.Invoke($"متصل — {_members.Count} عضو");
    }

    // ── Host: peer disconnected ───────────────────────────────────────────────

    private void OnPeerDisconnected(int peerId, string reason)
    {
        _vnet.RemoveRoute(peerId);

        if (_members.TryRemove(peerId, out var member))
        {
            var left = member with { LeftAt = DateTime.UtcNow };
            if (RoomId != null) _repo.RecordLeave(RoomId, member.VirtualIP);

            if (IsHost) BroadcastMemberSync("leave", member.Username);

            MemberLeft?.Invoke(left);
            StatusChanged?.Invoke($"متصل — {_members.Count} عضو");
        }
    }

    // ── Guest: receive Handshake ──────────────────────────────────────────────

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
                HandleDisconnectNotice(frame.Payload.ToArray());
                break;
        }
    }

    private void HandleHandshakeResponse(byte[] data)
    {
        var hs = HandshakePayload.Deserialize(data);
        if (hs == null) return;

        RoomId = hs.RoomId;
        MyVIP  = hs.YourVIP;
        VipAssigned?.Invoke(hs.YourVIP);

        // Populate member list from Host's snapshot and notify UI for each member
        _members.Clear();
        foreach (var m in hs.Members)
        {
            var record = new MemberRecord(-2, m.Username, m.VirtualIP, DateTime.UtcNow);
            _members[m.Username.GetHashCode()] = record;
            MemberJoined?.Invoke(record);
        }

        StatusChanged?.Invoke($"عضو شدید — IP مجازی: {hs.YourVIP}");

        // Start virtual network with the assigned IP
        _ = Task.Run(async () =>
        {
            try
            {
                byte octet = byte.Parse(hs.YourVIP.Split('.')[3]);
                await _vnet.StartAsync(_p2p, isHost: false, guestOctet: octet);
                MyVIP = hs.YourVIP;
                StatusChanged?.Invoke($"شبکه مجازی فعال — IP: {hs.YourVIP}");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"خطا در شبکه مجازی: {ex.Message}");
            }
        });
    }

    private void HandleMemberSync(byte[] data)
    {
        var sync = MemberSyncPayload.Deserialize(data);
        if (sync == null) return;

        _members.Clear();
        foreach (var m in sync.Members)
            _members[m.Username.GetHashCode()] =
                new MemberRecord(-2, m.Username, m.VirtualIP, DateTime.UtcNow);

        if (sync.Event == "join")
            MemberJoined?.Invoke(new MemberRecord(-2, sync.Username, "", DateTime.UtcNow));
        else
            MemberLeft?.Invoke(new MemberRecord(-2, sync.Username, "", DateTime.UtcNow));

        StatusChanged?.Invoke($"متصل — {_members.Count} عضو");
    }

    // ── Handshake / Sync helpers ──────────────────────────────────────────────

    private void SendHandshakeResponse(int peerId, string guestVIP)
    {
        var hs = new HandshakePayload
        {
            RoomId  = RoomId ?? "",
            YourVIP = guestVIP,
            HostVIP = HostVIP,
            Members = BuildMemberDtos()
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
            Members  = BuildMemberDtos()
        };
        _p2p.SendToAll(MessageType.MemberSync, sync.Serialize(),
            DeliveryMethod.ReliableOrdered);
    }

    private MemberDto[] BuildMemberDtos()
        => _members.Values
            .Select(m => new MemberDto { Username = m.Username, VirtualIP = m.VirtualIP })
            .ToArray();

    // ── Disconnect notice ─────────────────────────────────────────────────────

    private void HandleDisconnectNotice(byte[] data)
    {
        string reason = Encoding.UTF8.GetString(data);
        if (reason == "host_close" && !IsHost)
        {
            IsActive = false; // prevent PeerDisconnected from overriding UI status
            RoomClosed?.Invoke("میزبان Room را بست");
        }
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    /// <summary>Host closes the room, notifies all guests, then disconnects.</summary>
    public async Task CloseRoomAsync()
    {
        if (!IsActive) return;
        _p2p.SendToAll(MessageType.Disconnect,
            Encoding.UTF8.GetBytes("host_close"),
            DeliveryMethod.ReliableOrdered);
        await Task.Delay(150); // let the message reach guests
        Shutdown();
    }

    /// <summary>Guest voluntarily leaves the room.</summary>
    public async Task LeaveRoomAsync()
    {
        if (!IsActive) return;
        _p2p.SendToAll(MessageType.Disconnect,
            Encoding.UTF8.GetBytes("guest_leave"),
            DeliveryMethod.ReliableOrdered);
        await Task.Delay(50);
        Shutdown();
    }

    public void Shutdown()
    {
        IsActive = false;
        _vnet.Stop();
        _p2p.Shutdown();
        _members.Clear();
        MyVIP = null;
    }

    private static string GenerateRoomId()
        => Guid.NewGuid().ToString("N")[..8].ToUpper();

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _p2p.PeerConnected    -= OnPeerConnected;
        _p2p.PeerDisconnected -= OnPeerDisconnected;
        _p2p.MessageReceived  -= OnMessageReceived;

        Shutdown();
        _vnet.Dispose();
        _p2p.Dispose();
        _db.Dispose();
    }
}
