using System.Text.Json;
using System.Text.Json.Serialization;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using LiteNetLib;

namespace VirtualLANPlatform.Core.Chat;

/// <summary>
/// Sends and receives TextChat messages over the P2P layer (Phase 6).
/// Messages are encrypted automatically by P2PManager (Phase 5).
/// </summary>
public sealed class ChatManager : IDisposable
{
    private readonly P2PManager _p2p;
    private string _myUsername = "کاربر";

    public event Action<ChatMessage>? MessageReceived;

    public ChatManager(P2PManager p2p)
    {
        _p2p = p2p;
        _p2p.MessageReceived += OnP2PMessage;
    }

    public void SetUsername(string username) => _myUsername = username;

    // ── Send ──────────────────────────────────────────────────────────────────

    public void SendToAll(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !_p2p.IsRunning) return;

        var payload = new ChatPayload(_myUsername, text.Trim(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        _p2p.SendToAll(MessageType.TextChat, payload.Serialize(),
            DeliveryMethod.ReliableOrdered);

        // Echo to self
        MessageReceived?.Invoke(new ChatMessage(_myUsername, text.Trim(),
            DateTime.Now, IsOwn: true));
    }

    // ── Receive ───────────────────────────────────────────────────────────────

    private void OnP2PMessage(int peerId, MessageFrame frame)
    {
        if (frame.Type != MessageType.TextChat) return;

        var payload = ChatPayload.Deserialize(frame.Payload.ToArray());
        if (payload == null) return;

        var msg = new ChatMessage(
            payload.From,
            payload.Text,
            DateTimeOffset.FromUnixTimeSeconds(payload.At).LocalDateTime,
            IsOwn: false);

        MessageReceived?.Invoke(msg);
    }

    public void Dispose() => _p2p.MessageReceived -= OnP2PMessage;
}

// ── Wire format ───────────────────────────────────────────────────────────────

file sealed class ChatPayload
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("at")]   public long   At   { get; set; }

    public ChatPayload() { }
    public ChatPayload(string from, string text, long at)
        => (From, Text, At) = (from, text, at);

    public byte[] Serialize()   => JsonSerializer.SerializeToUtf8Bytes(this);
    public static ChatPayload? Deserialize(byte[] data)
        => JsonSerializer.Deserialize<ChatPayload>(data);
}
