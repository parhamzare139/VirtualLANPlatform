namespace VirtualLANPlatform.Core.Room;

public sealed record MemberRecord(
    int      PeerId,
    string   Username,
    DateTime JoinedAt)
{
    public DateTime? LeftAt { get; init; }
}
