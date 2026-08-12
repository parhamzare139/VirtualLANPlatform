using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualLANPlatform.Core.Room;

public sealed class HandshakePayload
{
    [JsonPropertyName("roomId")]  public string      RoomId  { get; set; } = "";
    [JsonPropertyName("members")] public MemberDto[] Members { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static HandshakePayload? Deserialize(byte[] data)
        => JsonSerializer.Deserialize<HandshakePayload>(data);
}

public sealed class MemberSyncPayload
{
    [JsonPropertyName("event")]    public string      Event    { get; set; } = "";
    [JsonPropertyName("username")] public string      Username { get; set; } = "";
    [JsonPropertyName("members")]  public MemberDto[] Members  { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static MemberSyncPayload? Deserialize(byte[] data)
        => JsonSerializer.Deserialize<MemberSyncPayload>(data);
}

public sealed class MemberDto
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
}
