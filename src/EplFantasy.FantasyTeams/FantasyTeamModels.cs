namespace EplFantasy.FantasyTeams;

// Persistence shapes for the Fantasy Team context (Architecture v1.15 §6.3; physical schema:
// 06-database-migrations/migrations/V005__fantasy_team.sql).

public enum FantasyTeamStatus
{
    Active,
    Withdrawn,
}

public enum AcquisitionType
{
    InitialDraft,
    SecondaryDraft,
    Replacement,
}

public class FantasyTeam
{
    public Guid FantasyTeamId { get; set; }
    public Guid LeagueMembershipId { get; set; }
    public Guid SeasonId { get; set; }
    public FantasyTeamStatus Status { get; set; } = FantasyTeamStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// IT-11 (F-002.3, BR-018/BR-019): represents the caller's own competition identity within one
    /// specific League+Season — always `Active` from the moment it's created. BR-193/Invariant 2's
    /// "at most one per (LeagueMembership, Season)" is enforced by IFantasyTeamService's own
    /// pre-check plus the database's `ux_fantasy_teams_membership_season` unique index (V005), not
    /// by this factory — it has no way to know about any other FantasyTeam row on its own.
    /// </summary>
    public static FantasyTeam Create(Guid fantasyTeamId, Guid leagueMembershipId, Guid seasonId, DateTimeOffset now) =>
        new()
        {
            FantasyTeamId = fantasyTeamId,
            LeagueMembershipId = leagueMembershipId,
            SeasonId = seasonId,
            Status = FantasyTeamStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
}

public class SquadPlayer
{
    public Guid SquadPlayerId { get; set; }
    public Guid FantasyTeamId { get; set; }
    public Guid PlayerId { get; set; }
    public Guid SeasonId { get; set; }
    public AcquisitionType AcquisitionType { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public bool IsCurrentlyOwned { get; set; } = true;
    public DateTimeOffset? ReplacementEligibleAt { get; set; }
}
