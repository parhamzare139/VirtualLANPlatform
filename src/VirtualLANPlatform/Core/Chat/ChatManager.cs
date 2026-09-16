using System.Text.Json;
using System.Text.Json.Serialization;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using LiteNetLib;

using VirtualLANPlatform.UI.Localization;

namespace VirtualLANPlatform.Core.Chat;

/// <summary>
/// Sends and receives TextChat messages over the P2P layer (Phase 6).
/// Messages are encrypted automatically by P2PManager (Phase 5).
/// </summary>
public sealed class ChatManager : IDisposable
{
    private readonly P2PManager _p2p;
    private string _myUsername = Loc.T("Chat_DefaultUser");

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>?      MessageDeleted;
    /// <summary>(username, isTyping) — someone started or stopped writing.</summary>
    public event Action<string, bool>? TypingChanged;

    private DateTime _lastTypingSent = DateTime.MinValue;
    private bool     _typingOn;

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
        _typingOn = false; // receivers clear the indicator when the message lands

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

    /// <summary>
    /// Announces composer activity. "Typing" is re-sent at most every 2.5 s while text
    /// keeps changing (receivers expire it after 5 s), and "stopped" goes out once.
    /// Unreliable: a lost one is corrected by the next, and it must never queue
    /// behind a big chat backlog.
    /// </summary>
    public void NotifyTyping(bool typing)
    {
        if (!_p2p.IsRunning) return;
        var now = DateTime.UtcNow;
        if (typing)
        {
            if (_typingOn && (now - _lastTypingSent).TotalSeconds < 2.5) return;
            _lastTypingSent = now;
        }
        else if (!_typingOn) return;
        _typingOn = typing;

        _p2p.SendToAll(MessageType.Typing,
            new TypingPayload { From = _myUsername, Typing = typing }.Serialize(),
            DeliveryMethod.Unreliable);
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

            case MessageType.Typing:
            {
                var t = TypingPayload.Deserialize(frame.Payload.ToArray());
                if (t is { From.Length: > 0 } && t.From != _myUsername)
                    TypingChanged?.Invoke(t.From, t.Typing);
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

file sealed class TypingPayload
{
    [JsonPropertyName("u")] public string From   { get; set; } = "";
    [JsonPropertyName("t")] public bool   Typing { get; set; }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static TypingPayload? Deserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<TypingPayload>(data); }
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
