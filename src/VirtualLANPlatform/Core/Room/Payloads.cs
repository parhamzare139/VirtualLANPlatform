using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualLANPlatform.Core.Room;

/// <summary>DTO carried in the Handshake message: Host → Guest.</summary>
public sealed class HandshakePayload
{
    [JsonPropertyName("roomId")]    public string     RoomId      { get; set; } = "";
    [JsonPropertyName("yourVIP")]   public string     YourVIP     { get; set; } = "";
    [JsonPropertyName("hostVIP")]   public string     HostVIP     { get; set; } = "";
    [JsonPropertyName("members")]   public MemberDto[] Members    { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static HandshakePayload? Deserialize(byte[] data)
        => JsonSerializer.Deserialize<HandshakePayload>(data);
}

/// <summary>DTO carried in the MemberSync message: Host → All peers.</summary>
public sealed class MemberSyncPayload
{
    [JsonPropertyName("event")]     public string     Event    { get; set; } = "";
    [JsonPropertyName("username")]  public string     Username { get; set; } = "";
    [JsonPropertyName("members")]   public MemberDto[] Members { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static MemberSyncPayload? Deserialize(byte[] data)
        => JsonSerializer.Deserialize<MemberSyncPayload>(data);
}

/// <summary>Compact member info embedded in Handshake and MemberSync payloads.</summary>
public sealed class MemberDto
{
    [JsonPropertyName("username")]  public string Username  { get; set; } = "";
    [JsonPropertyName("vip")]       public string VirtualIP { get; set; } = "";
}
