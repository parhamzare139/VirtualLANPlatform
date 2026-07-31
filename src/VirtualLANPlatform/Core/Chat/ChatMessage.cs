namespace VirtualLANPlatform.Core.Chat;

public sealed record ChatMessage(
    string   Sender,
    string   Text,
    DateTime SentAt,
    bool     IsOwn   // true when sent by this peer
);
