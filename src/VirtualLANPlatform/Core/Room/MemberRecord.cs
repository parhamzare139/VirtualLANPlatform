using VirtualLANPlatform.Core.Services;

namespace VirtualLANPlatform.Core.Room;

public sealed record MemberRecord(
    int      PeerId,
    string   Username,
    DateTime JoinedAt)
{
    public DateTime? LeftAt { get; init; }
    public Presence  Status { get; init; } = Presence.Online;
}
