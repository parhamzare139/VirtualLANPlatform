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
    public event Action<string>?      MessageDeleted;

    public ChatManager(P2PManager p2p)
    {
        _p2p = p2p;
        _p2p.MessageReceived += OnP2PMessage;
    }

    public void SetUsername(string username) => _myUsername = username;

    // ── Send ──────────────────────────────────────────────────────────────────

    /// <summary>Broadcasts <paramref name="text"/>, optionally as a reply to <paramref name="replyTo"/>.</summary>
    public void SendToAll(string text, ChatMessage? replyTo = null)
    {
        if (string.IsNullOrWhiteSpace(text) || !_p2p.IsRunning) return;

        string body = text.Trim();
        string id   = Guid.NewGuid().ToString("N")[..12];

        var payload = new ChatPayload
        {
            Id            = id,
            From          = _myUsername,
            Text          = body,
            At            = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ReplyToId     = replyTo?.Id,
            ReplyToSender = replyTo?.Sender,
            ReplyToText   = replyTo?.Preview
        };

        _p2p.SendToAll(MessageType.TextChat, payload.Serialize(), DeliveryMethod.ReliableOrdered);

        // Echo to self
        MessageReceived?.Invoke(new ChatMessage
        {
            Id            = id,
            Sender        = _myUsername,
            Text          = body,
            SentAt        = DateTime.Now,
            IsOwn         = true,
            ReplyToId     = replyTo?.Id,
            ReplyToSender = replyTo?.Sender,
            ReplyToText   = replyTo?.Preview
        });
    }

    /// <summary>Asks every peer to tombstone the message with this id, then does so locally.</summary>
    public void DeleteMessage(string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return;

        if (_p2p.IsRunning)
            _p2p.SendToAll(MessageType.ChatControl,
                new ChatControlPayload { Op = "delete", Id = messageId }.Serialize(),
                DeliveryMethod.ReliableOrdered);

        MessageDeleted?.Invoke(messageId);
    }

    // ── Receive ───────────────────────────────────────────────────────────────

    private void OnP2PMessage(int peerId, MessageFrame frame)
    {
        switch (frame.Type)
        {
            case MessageType.TextChat:
            {
                var payload = ChatPayload.Deserialize(frame.Payload.ToArray());
                if (payload == null) return;

                MessageReceived?.Invoke(new ChatMessage
                {
                    Id            = string.IsNullOrEmpty(payload.Id)
                                        ? Guid.NewGuid().ToString("N")[..12]
                                        : payload.Id,
                    Sender        = payload.From,
                    Text          = payload.Text,
                    SentAt        = DateTimeOffset.FromUnixTimeSeconds(payload.At).LocalDateTime,
                    IsOwn         = false,
                    ReplyToId     = payload.ReplyToId,
                    ReplyToSender = payload.ReplyToSender,
                    ReplyToText   = payload.ReplyToText
                });
                break;
            }

            case MessageType.ChatControl:
            {
                var ctrl = ChatControlPayload.Deserialize(frame.Payload.ToArray());
                if (ctrl is { Op: "delete", Id.Length: > 0 })
                    MessageDeleted?.Invoke(ctrl.Id);
                break;
            }
        }
    }

    public void Dispose() => _p2p.MessageReceived -= OnP2PMessage;
}

// ── Wire format ───────────────────────────────────────────────────────────────

file sealed class ChatPayload
{
    [JsonPropertyName("id")]   public string Id   { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("at")]   public long   At   { get; set; }

    [JsonPropertyName("rid")]  public string? ReplyToId     { get; set; }
    [JsonPropertyName("rfrom")]public string? ReplyToSender { get; set; }
    [JsonPropertyName("rtext")]public string? ReplyToText   { get; set; }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static ChatPayload? Deserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<ChatPayload>(data); }
        catch { return null; }
    }
}

file sealed class ChatControlPayload
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static ChatControlPayload? Deserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<ChatControlPayload>(data); }
        catch { return null; }
    }
}
