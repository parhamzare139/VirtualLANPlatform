using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualLANPlatform.Core.Room;

public sealed class HandshakePayload
{
    [JsonPropertyName("roomId")]  public string      RoomId  { get; set; } = "";
    [JsonPropertyName("members")] public MemberDto[] Members { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static HandshakePayload? Deserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<HandshakePayload>(data); }
        catch { return null; }
    }
}

public sealed class MemberSyncPayload
{
    [JsonPropertyName("event")]    public string      Event    { get; set; } = "";
    [JsonPropertyName("username")] public string      Username { get; set; } = "";
    [JsonPropertyName("members")]  public MemberDto[] Members  { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static MemberSyncPayload? Deserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<MemberSyncPayload>(data); }
        catch { return null; }
    }
}

public sealed class MemberDto
{
    [JsonPropertyName("username")] public string  Username { get; set; } = "";
    [JsonPropertyName("status")]   public string? Status   { get; set; }
}

/// <summary>What a member is doing: online / game / away. Sent by its owner, relayed by the host.</summary>
public sealed class PresencePayload
{
    [JsonPropertyName("u")] public string Username { get; set; } = "";
    [JsonPropertyName("s")] public string Status   { get; set; } = "online";

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static PresencePayload? Deserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<PresencePayload>(data); }
        catch { return null; }
    }
}

/// <summary>Host → guest command. <see cref="Op"/> is one of mute / kick / stopshare.</summary>
public sealed class ModerationPayload
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("on")] public bool   On { get; set; }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static ModerationPayload? Deserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<ModerationPayload>(data); }
        catch { return null; }
    }
}
