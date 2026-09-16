namespace VirtualLANPlatform.Core.Protocol;

public enum MessageType : byte
{
    Handshake        = 0x01,
    KeyExchange      = 0x02, // Reserved — Phase 5 (Encryption)
    RoomControl      = 0x03,
    MemberSync       = 0x04,
    TextChat         = 0x05,
    VoiceData        = 0x06,
    FileInfo         = 0x07,
    FileChunk        = 0x08,
    VirtualLanPacket = 0x09,
    KeepAlive        = 0x0A,
    Error            = 0x0B,
    Disconnect       = 0x0C,
    FileAccept        = 0x0D,
    ScreenShareStart  = 0x0E,
    ScreenShareFrame  = 0x0F,
    ScreenShareStop   = 0x10,
    ScreenShareAudio  = 0x11,
    ChatControl       = 0x12, // delete / edit of an existing chat message
    Moderation        = 0x13, // host → guest: force-mute, kick, stop screen share
    VirtualLanControl  = 0x14, // virtual-LAN IP assignment / control (host ↔ guest)
    Presence           = 0x15, // {"u":name,"s":online|game|away} — what a member is up to
    Typing             = 0x16, // {"u":name,"t":bool} — composer activity
    Relayed            = 0x17  // host → guest: a frame from another guest, [origin:4][type:1][payload]
}
