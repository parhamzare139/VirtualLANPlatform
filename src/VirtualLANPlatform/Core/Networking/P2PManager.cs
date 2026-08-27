using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LiteNetLib;
using LiteNetLib.Utils;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Security;

namespace VirtualLANPlatform.Core.Networking;

public enum PeerRole { None, Host, Guest }

public record PeerInfo(int Id, string Username, IPEndPoint EndPoint, DateTime ConnectedAt);

public sealed class P2PManager : INetEventListener, IDisposable
{
    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<PeerInfo>?          PeerConnected;
    public event Action<int, string>?       PeerDisconnected;
    public event Action<int, MessageFrame>? MessageReceived;
    public event Action<string>?            StatusChanged;
    public event Action<string, string>?    ConnectionFailed;
    public event Action<int, bool>?         EncryptionEstablished;
    public event Action<string>?            ConnectionRejected;

    public Func<string, IPEndPoint, string?>? ValidateGuest;

    // ── State ─────────────────────────────────────────────────────────────────

    public PeerRole Role      { get; private set; } = PeerRole.None;
    public bool     IsRunning { get; private set; }
    public int      PeerCount => _peers.Count;

    public bool IsEncrypted(int peerId) => _sessionKeys.ContainsKey(peerId);

    // Written on the dedicated P2P-Poll thread (every LiteNetLib callback runs there)
    // and read from the UI thread via PeerCount/IsEncrypted — must be thread-safe.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, PeerInfo>  _peers            = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pendingUsernames = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>    _sessionKeys      = new();
    private readonly TransportLayer             _transport        = new();
    private readonly CryptoEngine               _crypto           = new();
    private NetManager?               _net;
    private CancellationTokenSource?  _pollCts;
    private Thread?                   _pollThread;

    // ── Host ──────────────────────────────────────────────────────────────────

    public Task<(bool Ok, string LocalIP, ushort Port)> StartAsHostAsync(
        string username, ushort port, string? localIp = null, CancellationToken ct = default)
    {
        Role = PeerRole.Host;
        _net = BuildNetManager();

        bool started = localIp != null && IPAddress.TryParse(localIp, out var bindIp)
            ? _net.Start(bindIp.ToString(), "::1", port)
            : _net.Start(port);

        if (!started)
        {
            ConnectionFailed?.Invoke("Host شروع نشد", $"پورت {port} در دسترس نیست.");
            return Task.FromResult((false, "", (ushort)0));
        }

        IsRunning = true;
        StartPollLoop();
        StatusChanged?.Invoke("آماده — منتظر اتصال");
        return Task.FromResult((true, localIp ?? GetLocalIP(), port));
    }

    // ── Guest ─────────────────────────────────────────────────────────────────

    public async Task<bool> ConnectAsGuestAsync(
        string hostIp, ushort hostPort, string username, CancellationToken ct = default)
    {
        Role = PeerRole.Guest;

        if (_net != null && IsRunning)
        {
            _net.Stop();
            _net = null;
            _sessionKeys.Clear();
            _peers.Clear();
        }

        _net = BuildNetManager();
        if (!_net.Start())
        {
            ConnectionFailed?.Invoke("Guest شروع نشد", "خطا در راه‌اندازی شبکه.");
            return false;
        }

        IsRunning = true;
        StartPollLoop();
        StatusChanged?.Invoke("در حال اتصال...");

        var authData = new NetDataWriter();
        authData.Put(username);

        var peer = _net.Connect(hostIp, hostPort, authData);
        if (peer == null)
        {
            Shutdown();
            ConnectionFailed?.Invoke("اتصال شکست خورد", "آدرس یا پورت نامعتبر است.");
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
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ConnectionFailed?.Invoke("Timeout", "هاست پاسخ نداد — آدرس را بررسی کنید.");
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

    public void DisconnectPeer(int peerId)
        => _net?.GetPeerById(peerId)?.Disconnect();

    // ── INetEventListener ────────────────────────────────────────────────────

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (Role == PeerRole.Host)
        {
            string username;
            try
            {
                // A malformed/hostile connect request (not a valid length-prefixed
                // string) would otherwise throw here and, uncaught, kill the poll thread.
                username = request.Data.AvailableBytes > 0
                    ? request.Data.GetString()
                    : $"Guest_{request.RemoteEndPoint}";
            }
            catch
            {
                request.Reject();
                return;
            }

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

    public void OnPeerConnected(NetPeer peer)
    {
        string epKey   = peer.EndPoint.ToString();
        string username = Role == PeerRole.Host
            ? (_pendingUsernames.TryGetValue(epKey, out string? u) ? u : $"Guest_{peer.Id}")
            : "Host";
        _pendingUsernames.TryRemove(epKey, out _);

        var info = new PeerInfo(peer.Id, username, peer.EndPoint, DateTime.UtcNow);
        _peers[peer.Id] = info;

        SendKeyExchange(peer);

        StatusChanged?.Invoke($"متصل — {_peers.Count} کاربر");
        PeerConnected?.Invoke(info);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo di)
    {
        _peers.TryRemove(peer.Id, out _);
        _sessionKeys.TryRemove(peer.Id, out _);

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
            DisconnectReason.ConnectionFailed      => "اتصال برقرار نشد",
            DisconnectReason.Timeout               => "اتصال Timeout شد",
            DisconnectReason.RemoteConnectionClose => "طرف مقابل اتصال را قطع کرد",
            DisconnectReason.HostUnreachable       => "هاست در دسترس نیست",
            _                                      => di.Reason.ToString()
        };

        if (_peers.Count == 0 && di.Reason == DisconnectReason.RemoteConnectionClose)
            StatusChanged?.Invoke("قطع شده");

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
                return;
            }

            if (_sessionKeys.TryGetValue(peer.Id, out byte[]? key))
            {
                try
                {
                    byte[] plain = CryptoEngine.Decrypt(key, frame.Payload.ToArray());
                    frame = new MessageFrame(frame.Type, plain);
                }
                catch (CryptographicException) { return; }
            }

            MessageReceived?.Invoke(peer.Id, frame);
        }
        catch { }
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        => ConnectionFailed?.Invoke("خطای شبکه", $"{socketError}");
    public void OnNetworkReceiveUnconnected(IPEndPoint _, NetPacketReader __, UnconnectedMessageType ___) { }

    // ── Key exchange ──────────────────────────────────────────────────────────

    private void SendKeyExchange(NetPeer peer)
    {
        byte[] pubKey = _crypto.ExportPublicKey();
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
        IPv6Enabled                = false,
        UnconnectedMessagesEnabled = false,
        PingInterval               = 2000,
        DisconnectTimeout          = 30000
    };

    // Voice is the latency-critical consumer of PollEvents: whatever the poll
    // interval is, it lands on every received audio frame. Task.Delay can't go
    // below the ~15 ms system tick, so we run a dedicated thread and raise the
    // timer resolution to 1 ms for as long as the session is live (the same
    // trade every real-time voice client makes).
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);

    private void StartPollLoop()
    {
        // Reconnecting as a guest builds a fresh NetManager; without this the old
        // thread would survive, poll the new manager too, and leak a 1 ms timer.
        // Joining (not just signaling cancellation) matters here: StartPollLoop is
        // always called right after a new NetManager is assigned to _net, so if the
        // old thread is still mid-iteration it would read the *new* _net on its next
        // pass and call PollEvents() on it concurrently with the new poll thread —
        // LiteNetLib's NetManager isn't designed for concurrent PollEvents() calls.
        _pollCts?.Cancel();
        _pollThread?.Join(500);

        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        _pollThread = new Thread(() =>
        {
            bool raised = false;
            try { raised = TimeBeginPeriod(1) == 0; } catch { }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Snapshot the field once — reading it twice (a null-check then a
                    // call) races against another thread setting _net = null between
                    // the two, which throws NullReferenceException on this thread.
                    var net = _net;
                    if (net == null) break;

                    try { net.PollEvents(); }
                    catch (ObjectDisposedException) { break; }
                    // PollEvents synchronously invokes every INetEventListener callback
                    // (OnPeerConnected, OnConnectionRequest, OnNetworkReceive, ...). An
                    // unhandled exception on this background thread would otherwise
                    // terminate the whole process — one bad packet/callback shouldn't
                    // kill the entire P2P session.
                    catch { }

                    Thread.Sleep(1);
                }
            }
            finally
            {
                if (raised) { try { TimeEndPeriod(1); } catch { } }
            }
        })
        {
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal,
            Name         = "P2P-Poll"
        };
        _pollThread.Start();
    }

    private static NetDataWriter WrapPayload(byte[] data)
    {
        var w = new NetDataWriter();
        w.Put(data);
        return w;
    }

    public static string GetLocalIP()
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
        _pollThread?.Join(500);
        _crypto.Dispose();
        _pollCts?.Dispose();
    }
}
