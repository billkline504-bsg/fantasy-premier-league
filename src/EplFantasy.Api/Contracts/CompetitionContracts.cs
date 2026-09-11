namespace EplFantasy.Api.Contracts;

// IT-41 (F-009.4): mirrors OpenAPI's HeadToHeadMatch schema exactly (see AuthContracts.cs's own
// header comment on why these DTOs carry no validation attributes — this one is response-only).

public sealed class HeadToHeadMatchDto
{
    public required Guid MatchId { get; init; }
    public required Guid SeasonId { get; init; }
    public required Guid GameweekId { get; init; }
    public required Guid HomeFantasyTeamId { get; init; }
    public required Guid AwayFantasyTeamId { get; init; }
    public int? HomeScore { get; init; }
    public int? AwayScore { get; init; }
    public string? Result { get; init; }
    public int? LeaguePointsHome { get; init; }
    public int? LeaguePointsAway { get; init; }
}

// IT-44 (F-010.4): mirrors OpenAPI's LeagueStanding schema exactly — note it carries no SeasonId
// of its own (unlike HeadToHeadMatchDto above), matching the spec precisely.

public sealed class LeagueStandingDto
{
    public required Guid FantasyTeamId { get; init; }
    public required int LeaguePoints { get; init; }
    public required int Played { get; init; }
    public required int Won { get; init; }
    public required int Drawn { get; init; }
    public required int Lost { get; init; }
    public required int FantasyGoalsFor { get; init; }
    public required int FantasyGoalsAgainst { get; init; }
    public required int FantasyGoalDifference { get; init; }
    public required int CaptainPointsTotal { get; init; }
    public required int Position { get; init; }
    public required Guid AsOfGameweekId { get; init; }
}
