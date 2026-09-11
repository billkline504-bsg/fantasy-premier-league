using System.ComponentModel.DataAnnotations;
using EplFantasy.Leagues;

namespace EplFantasy.Api.Contracts;

// IT-10 (F-002.2): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// setLeagueIcon's OpenAPI request body exactly.
public sealed class SetLeagueIconRequest
{
    /// <summary>Nullable so [Required] can actually detect a missing value — see CreateSeasonRequest.StartDate's own remarks on why a non-nullable value type can't.</summary>
    [Required]
    public Guid? ProfileIconId { get; set; }
}

/// <summary>
/// IT-04/IT-05: OpenAPI's LeagueMembership schema — Username/EffectiveIconId are composed from the
/// User/UserProfile a plain LeagueMembership row doesn't itself carry. Shared by acceptInvitation
/// (InvitationController) and listMemberships/getMembership (MembershipController) — the same
/// composed shape, not two independent DTOs that happen to look alike.
/// </summary>
public sealed class LeagueMembershipDto
{
    public required Guid LeagueMembershipId { get; init; }
    public required Guid LeagueId { get; init; }
    public required Guid UserId { get; init; }
    public required string Username { get; init; }
    public required bool IsAdministrator { get; init; }
    public Guid? LeagueIconId { get; init; }
    public required Guid EffectiveIconId { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
    public DateTimeOffset? LeftAt { get; init; }

    public static LeagueMembershipDto From(LeagueMembership membership, string username, Guid defaultIconId) => new()
    {
        LeagueMembershipId = membership.LeagueMembershipId,
        LeagueId = membership.LeagueId,
        UserId = membership.UserId,
        Username = username,
        IsAdministrator = membership.IsAdministrator,
        LeagueIconId = membership.LeagueIconId,
        EffectiveIconId = membership.LeagueIconId ?? defaultIconId,
        Status = membership.Status.ToString(),
        JoinedAt = membership.JoinedAt,
        LeftAt = membership.LeftAt,
    };
}
