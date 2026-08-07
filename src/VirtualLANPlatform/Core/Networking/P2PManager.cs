using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LiteNetLib;
using LiteNetLib.Utils;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Security;

namespace VirtualLANPlatform.Core.Networking;

public enum PeerRole { None, Host, Guest }

public record PeerInfo(int Id, string Username, IPEndPoint EndPoint, DateTime ConnectedAt);

/// <summary>
/// Core P2P connection manager built on LiteNetLib (UDP).
///
/// Phase 5: automatic ECDH key exchange on every new peer connection.
/// After exchange, all payloads are encrypted with AES-256-GCM.
/// KeyExchange messages themselves are always sent in plaintext.
/// </summary>
public sealed class P2PManager : INetEventListener, IDisposable
{
    // ── Public events ─────────────────────────────────────────────────────────

    public event Action<PeerInfo>?             PeerConnected;
    public event Action<int, string>?          PeerDisconnected;
    public event Action<int, MessageFrame>?    MessageReceived;
    public event Action<string>?               StatusChanged;
    public event Action<string, string>?       ConnectionFailed;
    public event Action<int, bool>?            EncryptionEstablished; // peerId, isEncrypted
    public event Action<string>?               ConnectionRejected;    // fires on Guest with rejection reason

    // Host sets this to validate incoming guests: return null to accept, error string to reject
    public Func<string, IPEndPoint, string?>?  ValidateGuest;

    // ── State ─────────────────────────────────────────────────────────────────

    public PeerRole   Role       { get; private set; } = PeerRole.None;
    public bool       IsRunning  { get; private set; }
    public int        PeerCount  => _peers.Count;
    public NatStatus  NatStatus  => _nat.Status;

    // Returns true if the given peer has an established encryption session
    public bool IsEncrypted(int peerId) => _sessionKeys.ContainsKey(peerId);

    private readonly Dictionary<int, PeerInfo>  _peers            = [];
    private readonly Dictionary<string, string> _pendingUsernames = [];
    private readonly Dictionary<int, byte[]>    _sessionKeys      = []; // peerId → AES key
    private readonly TransportLayer             _transport        = new();
    private readonly UPnPManager               _upnp             = new();
    private readonly PublicIPDiscovery          _ipDisc           = new();
    private readonly NatTraversal              _nat;
    private readonly CryptoEngine              _crypto            = new();
    private NetManager? _net;
    private CancellationTokenSource? _pollCts;

    public P2PManager()
    {
        _nat = new NatTraversal(_upnp, _ipDisc);
    }

    // ── Host ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns LanCode (local IP — works on same machine and same LAN)
    /// and InternetCode (public IP — works over the internet when UPnP/port-forward is active).
    /// </summary>
    public async Task<(bool Ok, string LanCode, string InternetCode)> StartAsHostAsync(
        string username, ushort port, CancellationToken ct = default)
    {
        Role = PeerRole.Host;
        _net = BuildNetManager();

        if (!_net.Start(port))
        {
            ConnectionFailed?.Invoke("Host شروع نشد", $"پورت {port} در دسترس نیست.");
            return (false, "", "");
        }

        IsRunning = true;
        StartPollLoop();
        StatusChanged?.Invoke("در حال کشف IP عمومی و NAT...");

        string localIp = GetLocalIP();
        await _nat.PrepareHostAsync(port, localIp, ct);

        string lanCode      = ConnectionCodeEngine.Encode(IPAddress.Parse(localIp), port);
        string internetCode = "";
        if (_nat.Status.PublicIP is { } pub && pub != "کشف نشد")
        {
            ushort extPort = _nat.Status.ExternalPort ?? port;
            internetCode = ConnectionCodeEngine.Encode(IPAddress.Parse(pub), extPort);
        }

        StatusChanged?.Invoke("آماده — منتظر اتصال");
        return (true, lanCode, internetCode);
    }

    // ── Guest ─────────────────────────────────────────────────────────────────

    public async Task<bool> ConnectAsGuestAsync(
        string connectionCode, string username, CancellationToken ct = default)
    {
        Role = PeerRole.Guest;

        (IPAddress hostIp, ushort hostPort) target;
        try   { target = ConnectionCodeEngine.Decode(connectionCode); }
        catch (FormatException ex)
        {
            ConnectionFailed?.Invoke("Connection Code نامعتبر", ex.Message);
            return false;
        }

        _net = BuildNetManager();
        if (!_net.Start())
        {
            ConnectionFailed?.Invoke("Guest شروع نشد", "خطا در راه‌اندازی شبکه.");
            return false;
        }

        IsRunning = true;
        StartPollLoop();
        // ‪ = LTR embedding, ‬ = pop — prevents RTL font from rendering dots as slashes
        StatusChanged?.Invoke($"در حال اتصال به هاست ‪{target.hostIp}:{target.hostPort}‬ ...");

        var authData = new NetDataWriter();
        authData.Put(username);

        var peer = _net.Connect(target.hostIp.ToString(), target.hostPort, authData);
        if (peer == null)
        {
            Shutdown();
            ConnectionFailed?.Invoke("اتصال شکست خورد", NatTraversal.GetFailureGuidance());
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnConnected(PeerInfo _)    => tcs.TrySetResult(true);
        void OnFailed(int _, string __) => tcs.TrySetResult(false);

        PeerConnected    += OnConnected;
        PeerDisconnected += OnFailed;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ConnectionFailed?.Invoke("Timeout", NatTraversal.GetFailureGuidance());
            return false;
        }
        finally
        {
            PeerConnected    -= OnConnected;
            PeerDisconnected -= OnFailed;
        }
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    public void SendToAll(MessageType type, byte[] payload,
        DeliveryMethod method = DeliveryMethod.ReliableOrdered)
    {
        if (_net == null) return;
        foreach (var peer in _net)
            SendToPeerInternal(peer, type, payload, method);
    }

    public void SendToPeer(int peerId, MessageType type, byte[] payload,
        DeliveryMethod method = DeliveryMethod.ReliableOrdered)
    {
        if (_net?.GetPeerById(peerId) is not NetPeer peer) return;
        SendToPeerInternal(peer, type, payload, method);
    }

    private void SendToPeerInternal(NetPeer peer, MessageType type, byte[] payload,
        DeliveryMethod method)
    {
        // KeyExchange is always plaintext — everything else is encrypted if key is ready
        byte[] finalPayload = (type != MessageType.KeyExchange
            && _sessionKeys.TryGetValue(peer.Id, out byte[]? key))
            ? CryptoEngine.Encrypt(key, payload)
            : payload;

        peer.Send(WrapPayload(_transport.Pack(type, finalPayload)), method);
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public void Shutdown()
    {
        IsRunning = false;
        _pollCts?.Cancel();
        _net?.Stop();
        _sessionKeys.Clear();
    }

    // ── INetEventListener ────────────────────────────────────────────────────

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (Role == PeerRole.Host)
        {
            string username = request.Data.AvailableBytes > 0
                ? request.Data.GetString()
                : $"Guest_{request.RemoteEndPoint}";

            // Reject based on Host-side validation (e.g. duplicate username)
            string? rejection = ValidateGuest?.Invoke(username, request.RemoteEndPoint);
            if (rejection != null)
            {
                var w = new NetDataWriter();
                w.Put(rejection);
                request.Reject(w);
                return;
            }

            _pendingUsernames[request.RemoteEndPoint.ToString()] = username;
            request.Accept();
        }
        else
            request.Reject();
    }

    public void DisconnectPeer(int peerId)
        => _net?.GetPeerById(peerId)?.Disconnect();

    public void OnPeerConnected(NetPeer peer)
    {
        string epKey = peer.EndPoint.ToString();
        string username = Role == PeerRole.Host
            ? (_pendingUsernames.TryGetValue(epKey, out string? u) ? u : $"Guest_{peer.Id}")
            : "Host";
        _pendingUsernames.Remove(epKey);

        var info = new PeerInfo(peer.Id, username, peer.EndPoint, DateTime.UtcNow);
        _peers[peer.Id] = info;

        // Immediately send our public key — key exchange before anything else
        SendKeyExchange(peer);

        StatusChanged?.Invoke($"متصل — {_peers.Count} کاربر");
        PeerConnected?.Invoke(info);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo di)
    {
        _peers.Remove(peer.Id);
        _sessionKeys.Remove(peer.Id);

        // Connection was rejected by Host — fire rejection event and unblock ConnectAsGuestAsync
        if (di.Reason == DisconnectReason.ConnectionRejected)
        {
            string msg = di.AdditionalData.AvailableBytes > 0
                ? di.AdditionalData.GetString()
                : "اتصال توسط Host رد شد";
            ConnectionRejected?.Invoke(msg);
            PeerDisconnected?.Invoke(peer.Id, msg);
            return;
        }

        string reason = di.Reason switch
        {
            DisconnectReason.ConnectionFailed      => "اتصال برقرار نشد — " + NatTraversal.GetFailureGuidance(),
            DisconnectReason.Timeout               => "اتصال Timeout شد",
            DisconnectReason.RemoteConnectionClose => "طرف مقابل اتصال را قطع کرد",
            DisconnectReason.HostUnreachable       => NatTraversal.GetFailureGuidance(),
            _                                      => di.Reason.ToString()
        };

        StatusChanged?.Invoke(_peers.Count > 0 ? $"متصل — {_peers.Count} کاربر" : "قطع شده");
        PeerDisconnected?.Invoke(peer.Id, reason);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader,
        byte channelNumber, DeliveryMethod deliveryMethod)
    {
        try
        {
            byte[] data = reader.GetRemainingBytes();
            if (!_transport.TryUnpack(data, out MessageFrame frame)) return;

            if (frame.Type == MessageType.KeyExchange)
            {
                HandleKeyExchange(peer.Id, frame.Payload.ToArray());
                return; // don't forward KeyExchange to higher layers
            }

            // Decrypt if session key is established
            if (_sessionKeys.TryGetValue(peer.Id, out byte[]? key))
            {
                try
                {
                    byte[] plain = CryptoEngine.Decrypt(key, frame.Payload.ToArray());
                    frame = new MessageFrame(frame.Type, plain);
                }
                catch (CryptographicException)
                {
                    return; // tampered or wrong key — discard silently
                }
            }

            MessageReceived?.Invoke(peer.Id, frame);
        }
        catch { }
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        => ConnectionFailed?.Invoke("خطای شبکه", $"{socketError} — {endPoint}");

    public void OnNetworkReceiveUnconnected(IPEndPoint _, NetPacketReader __, UnconnectedMessageType ___) { }

    // ── Key exchange ──────────────────────────────────────────────────────────

    private void SendKeyExchange(NetPeer peer)
    {
        byte[] pubKey = _crypto.ExportPublicKey();
        // Send as plaintext — public keys are not secret
        peer.Send(WrapPayload(_transport.Pack(MessageType.KeyExchange, pubKey)),
            DeliveryMethod.ReliableOrdered);
    }

    private void HandleKeyExchange(int peerId, byte[] peerPublicKey)
    {
        try
        {
            byte[] sessionKey = _crypto.DeriveSessionKey(peerPublicKey);
            _sessionKeys[peerId] = sessionKey;
            EncryptionEstablished?.Invoke(peerId, true);
            StatusChanged?.Invoke($"رمزنگاری برقرار شد — Peer {peerId}");
        }
        catch
        {
            EncryptionEstablished?.Invoke(peerId, false);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private NetManager BuildNetManager() => new(this)
    {
        AutoRecycle                = true,
        IPv6Enabled                = true,
        UnconnectedMessagesEnabled = false,
        PingInterval               = 1000,
        DisconnectTimeout          = 10000
    };

    private void StartPollLoop()
    {
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _net != null)
            {
                _net.PollEvents();
                await Task.Delay(15, token).ConfigureAwait(false);
            }
        }, token);
    }

    private static NetDataWriter WrapPayload(byte[] data)
    {
        var w = new NetDataWriter();
        w.Put(data);
        return w;
    }

    private static string GetLocalIP()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);
            s.Connect("8.8.8.8", 80);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    public void Dispose()
    {
        Shutdown();
        _crypto.Dispose();
        _upnp.Dispose();
        _ipDisc.Dispose();
        _pollCts?.Dispose();
    }
}
