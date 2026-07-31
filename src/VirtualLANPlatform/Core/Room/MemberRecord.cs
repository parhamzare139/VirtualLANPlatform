namespace VirtualLANPlatform.Core.Room;

/// <summary>Live member entry kept by the Host while the Room is open.</summary>
public sealed record MemberRecord(
    int      PeerId,
    string   Username,
    string   VirtualIP,
    DateTime JoinedAt)
{
    /// <summary>Null until the member leaves.</summary>
    public DateTime? LeftAt { get; init; }
}
