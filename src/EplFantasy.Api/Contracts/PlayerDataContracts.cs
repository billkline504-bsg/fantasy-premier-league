using EplFantasy.PlayerData;

namespace EplFantasy.Api.Contracts;

// IT-17 (F-004.1): OpenAPI's Club/Player schemas — read-only reference data returned by
// listClubs/listPlayers/getPlayer. Player.Position is exposed as a string (player.Position.ToString())
// rather than the numeric enum, the same convention every other enum-carrying DTO in this codebase
// already uses (e.g. UserSelfDto.Status, LeagueMembershipDto.Status).

public sealed class ClubDto
{
    public required Guid ClubId { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }

    public static ClubDto From(Club club) => new()
    {
        ClubId = club.ClubId,
        Name = club.Name,
        ShortName = club.ShortName,
    };
}

public sealed class PlayerDto
{
    public required Guid PlayerId { get; init; }
    public required string EplPlayerId { get; init; }
    public required string Name { get; init; }
    public required string Position { get; init; }
    public Guid? CurrentClubId { get; init; }

    public static PlayerDto From(Player player) => new()
    {
        PlayerId = player.PlayerId,
        EplPlayerId = player.EplPlayerId,
        Name = player.Name,
        Position = player.Position.ToString(),
        CurrentClubId = player.CurrentClubId,
    };
}

// IT-18 (F-004.2): OpenAPI's Gameweek/Fixture schemas.

public sealed class GameweekDto
{
    public required Guid GameweekId { get; init; }
    public required string EplSeasonIdentifier { get; init; }
    public required int Number { get; init; }
    public required DateTimeOffset RosterLockDeadline { get; init; }

    public static GameweekDto From(Gameweek gameweek) => new()
    {
        GameweekId = gameweek.GameweekId,
        EplSeasonIdentifier = gameweek.EplSeasonIdentifier,
        Number = gameweek.Number,
        RosterLockDeadline = gameweek.RosterLockDeadline,
    };
}

public sealed class FixtureDto
{
    public required Guid FixtureId { get; init; }
    public required Guid GameweekId { get; init; }
    public required Guid HomeClubId { get; init; }
    public required Guid AwayClubId { get; init; }
    public required DateTimeOffset KickoffTime { get; init; }
    public required string Status { get; init; }
    public int? HomeGoals { get; init; }
    public int? AwayGoals { get; init; }

    public static FixtureDto From(Fixture fixture) => new()
    {
        FixtureId = fixture.FixtureId,
        GameweekId = fixture.GameweekId,
        HomeClubId = fixture.HomeClubId,
        AwayClubId = fixture.AwayClubId,
        KickoffTime = fixture.KickoffTime,
        Status = fixture.Status.ToString(),
        HomeGoals = fixture.HomeGoals,
        AwayGoals = fixture.AwayGoals,
    };
}

/// <summary>IT-19 (F-004.6): OpenAPI's ClubStanding schema — the real-world EPL table, BR-330's ten displayed columns.</summary>
public sealed class ClubStandingDto
{
    public required Guid ClubId { get; init; }
    public required int Position { get; init; }
    public required int Played { get; init; }
    public required int Won { get; init; }
    public required int Drawn { get; init; }
    public required int Lost { get; init; }
    public required int GoalsFor { get; init; }
    public required int GoalsAgainst { get; init; }
    public required int GoalDifference { get; init; }
    public required int Points { get; init; }

    public static ClubStandingDto From(ClubStanding standing) => new()
    {
        ClubId = standing.ClubId,
        Position = standing.Position,
        Played = standing.Played,
        Won = standing.Won,
        Drawn = standing.Drawn,
        Lost = standing.Lost,
        GoalsFor = standing.GoalsFor,
        GoalsAgainst = standing.GoalsAgainst,
        GoalDifference = standing.GoalDifference,
        Points = standing.Points,
    };
}
