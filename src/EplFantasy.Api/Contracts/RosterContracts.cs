using EplFantasy.Rosters;

namespace EplFantasy.Api.Contracts;

// IT-29 (F-007.1): getGameweekRoster/submitGameweekRoster's exact request/response OpenAPI schemas.

public sealed class RosterSubmissionRequest
{
    /// <summary>
    /// No [MinLength]/[MaxLength] here — SeasonConfiguration.WeeklyRosterSize (BR-279, ADR-011) is
    /// per-Season configurable, never a literal 15, so the real exact-size check is
    /// GameweekRoster.Submit's own, against the Season's actual configured value.
    /// </summary>
    public List<Guid> PlayerIds { get; set; } = [];

    /// <summary>Optional at submission time (OpenAPI Specification v1.0's own RosterSubmissionRequest note) — may instead be set afterward via setCaptain (IT-30). Omitted on a resubmission clears any previously-designated Captain, PUT-style full replacement, not a partial patch.</summary>
    public Guid? CaptainPlayerId { get; set; }
}

/// <summary>IT-30 (F-007.2): setCaptain's request body — captainPlayerId must be one of the roster's already-selected players (BR-046).</summary>
public sealed class SetCaptainRequest
{
    public Guid CaptainPlayerId { get; set; }
}

/// <summary>
/// IT-32 (F-007.4): correctRoster's request body. OpenAPI Specification v1.0's own schema requires
/// only <see cref="Reason"/> — both <see cref="PlayerIds"/> and <see cref="CaptainPlayerId"/> are
/// independently optional, so an Administrator may correct just the players, just the Captain, or
/// both in one call. An omitted (null) <see cref="PlayerIds"/> means "leave player selection
/// unchanged" (RosterService.CorrectAsync routes that case to GameweekRoster.CorrectCaptain rather
/// than a full replace); a null <see cref="CaptainPlayerId"/> always means "no Captain", the same
/// convention <see cref="RosterSubmissionRequest"/> already establishes, whether or not
/// <see cref="PlayerIds"/> is also supplied.
/// </summary>
public sealed class RosterCorrectionRequest
{
    public List<Guid>? PlayerIds { get; set; }
    public Guid? CaptainPlayerId { get; set; }
    public string Reason { get; set; } = null!;
}

public sealed class RosterPlayerDto
{
    public required Guid PlayerId { get; init; }
    public required bool IsCaptain { get; init; }
    public string? SelectionRole { get; init; }
    public Guid? OpponentClubId { get; init; }
    public bool? OpponentIsHome { get; init; }

    public static RosterPlayerDto From(
        RosterPlayer player,
        Guid? opponentClubId,
        bool? opponentIsHome) => new()
    {
        PlayerId = player.PlayerId,
        IsCaptain = player.IsCaptain,
        SelectionRole = player.SelectionRole?.ToString(),
        OpponentClubId = opponentClubId,
        OpponentIsHome = opponentIsHome,
    };
}

public sealed class GameweekRosterDto
{
    public required Guid GameweekRosterId { get; init; }
    public required Guid FantasyTeamId { get; init; }
    public required Guid GameweekId { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public DateTimeOffset? LockedAt { get; init; }
    public Guid? CaptainPlayerId { get; init; }
    public required IReadOnlyList<RosterPlayerDto> Players { get; init; }

    /// <summary>BR-336: the same rows GET /epl/gameweeks/{gameweekId}/fixtures (IT-18) already returns — no new query shape, just composed alongside the roster.</summary>
    public required IReadOnlyList<FixtureDto> GameweekFixtures { get; init; }
}
