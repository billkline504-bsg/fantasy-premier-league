using EplFantasy.FantasyTeams;
using EplFantasy.PlayerData;
using EplFantasy.Reporting;

namespace EplFantasy.Api.Contracts;

/// <summary>
/// IT-11 (F-002.3): OpenAPI's FantasyTeam schema — Username is composed from the User behind the
/// FantasyTeam's LeagueMembership, which a plain FantasyTeam row doesn't itself carry
/// (FantasyTeamController joins it at read time), the same composed-DTO pattern
/// LeagueMembershipDto already established.
/// </summary>
public sealed class FantasyTeamDto
{
    public required Guid FantasyTeamId { get; init; }
    public required Guid LeagueMembershipId { get; init; }
    public required Guid SeasonId { get; init; }
    public required string Username { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public static FantasyTeamDto From(FantasyTeam team, string username) => new()
    {
        FantasyTeamId = team.FantasyTeamId,
        LeagueMembershipId = team.LeagueMembershipId,
        SeasonId = team.SeasonId,
        Username = username,
        Status = team.Status.ToString(),
        CreatedAt = team.CreatedAt,
    };
}

/// <summary>
/// IT-25 (F-007.5): OpenAPI's SquadPlayerView schema — one row per SquadPlayer the FantasyTeam has
/// ever held, including a released one (IsCurrentlyOwned = false, still visible per BR-264's
/// retained acquisition history — the Implementation Task Breakdown's own Tests bullet names this
/// directly). ClubId is nullable here even though the OpenAPI schema itself doesn't mark it so —
/// Player.CurrentClubId genuinely is nullable once a player exits the EPL (BR-066), and this DTO
/// stays correct to that reality rather than to the spec's own oversight.
/// </summary>
public sealed class SquadPlayerViewDto
{
    public required Guid SquadPlayerId { get; init; }
    public required Guid PlayerId { get; init; }
    public required string PlayerName { get; init; }
    public Guid? ClubId { get; init; }
    public required string Position { get; init; }
    public required string AcquisitionType { get; init; }
    public required DateTimeOffset AcquiredAt { get; init; }
    public required bool IsCurrentlyOwned { get; init; }
    public required bool OnCurrentGameweekRoster { get; init; }
    public DateTimeOffset? ReplacementEligibleAt { get; init; }
    public required long SeasonMinutesPlayed { get; init; }
    public required long SeasonGamesPlayed { get; init; }
    public required long SeasonFantasyPoints { get; init; }

    /// <summary>
    /// IT-25's own Domain bullet: the current-Gameweek roster indicator (BR-319) has nothing to
    /// query yet — GameweekRoster/RosterPlayer rows are IT-29's job to ever create — so it's a
    /// literal false for every row until then, not a query against an always-empty table.
    /// </summary>
    public static SquadPlayerViewDto From(SquadPlayer squadPlayer, Player player, PlayerSeasonStatistics? stats) => new()
    {
        SquadPlayerId = squadPlayer.SquadPlayerId,
        PlayerId = player.PlayerId,
        PlayerName = player.Name,
        ClubId = player.CurrentClubId,
        Position = player.Position.ToString(),
        AcquisitionType = squadPlayer.AcquisitionType.ToString(),
        AcquiredAt = squadPlayer.AcquiredAt,
        IsCurrentlyOwned = squadPlayer.IsCurrentlyOwned,
        OnCurrentGameweekRoster = false,
        ReplacementEligibleAt = squadPlayer.ReplacementEligibleAt,
        SeasonMinutesPlayed = stats?.MinutesPlayed ?? 0,
        SeasonGamesPlayed = stats?.GamesPlayed ?? 0,
        SeasonFantasyPoints = stats?.FantasyPoints ?? 0,
    };
}
