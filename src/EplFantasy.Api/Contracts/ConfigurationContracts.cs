using System.ComponentModel.DataAnnotations;
using EplFantasy.Leagues;

namespace EplFantasy.Api.Contracts;

// IT-08 (F-003.5): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// OpenAPI's LeagueConfiguration/SeasonConfiguration schemas, including their nested
// PositionalMinimums/DraftTimerSecondsByType/LeaguePoints sub-objects, exactly. The request and
// response share the same nested sub-DTOs; only the top-level type differs (a request carries just
// the writable fields, a response adds the read-only ones: leagueId/seasonId/updatedAt/
// updatedByMembershipId/lockedFields).

public sealed class PositionalMinimumsDto
{
    [Range(0, int.MaxValue)]
    public int Gk { get; set; }

    [Range(0, int.MaxValue)]
    public int Def { get; set; }

    [Range(0, int.MaxValue)]
    public int Mid { get; set; }

    [Range(0, int.MaxValue)]
    public int Fwd { get; set; }
}

public sealed class DraftTimerSecondsByTypeDto
{
    [Range(1, int.MaxValue)]
    public int Initial { get; set; }

    [Range(1, int.MaxValue)]
    public int Secondary { get; set; }

    [Range(1, int.MaxValue)]
    public int Replacement { get; set; }
}

public sealed class LeaguePointsDto
{
    public int Win { get; set; }

    public int Draw { get; set; }

    public int Loss { get; set; }
}

public sealed class UpdateLeagueConfigurationRequest
{
    [Range(1, int.MaxValue)]
    public int InitialSquadSize { get; set; }

    [Range(1, int.MaxValue)]
    public int WeeklyRosterSize { get; set; }

    [Required]
    public PositionalMinimumsDto PositionalMinimums { get; set; } = null!;

    [Required]
    public DraftTimerSecondsByTypeDto DraftTimerSecondsByType { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int SecondaryDraftSelectionsPerTeam { get; set; }

    [Range(0, int.MaxValue)]
    public int SecondaryDraftSchedulingOffsetDays { get; set; }

    [Range(0, int.MaxValue)]
    public int GameweekRosterLockOffsetBeforeKickoffMinutes { get; set; }

    [Required]
    public LeaguePointsDto LeaguePoints { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int InvitationExpirationDays { get; set; }

    [Range(0, int.MaxValue)]
    public int? ReplacementSelectionCap { get; set; }

    [Range(0, int.MaxValue)]
    public int GameweekReminderLeadTimeHours { get; set; }

    [Required]
    public string TieBreakRulesetVersion { get; set; } = null!;

    public ConfigurationValues ToValues() => new(
        InitialSquadSize,
        WeeklyRosterSize,
        PositionalMinimums.Gk,
        PositionalMinimums.Def,
        PositionalMinimums.Mid,
        PositionalMinimums.Fwd,
        DraftTimerSecondsByType.Initial,
        DraftTimerSecondsByType.Secondary,
        DraftTimerSecondsByType.Replacement,
        SecondaryDraftSelectionsPerTeam,
        SecondaryDraftSchedulingOffsetDays,
        GameweekRosterLockOffsetBeforeKickoffMinutes,
        LeaguePoints.Win,
        LeaguePoints.Draw,
        LeaguePoints.Loss,
        InvitationExpirationDays,
        ReplacementSelectionCap,
        GameweekReminderLeadTimeHours,
        TieBreakRulesetVersion);
}

/// <summary>Same writable shape as <see cref="UpdateLeagueConfigurationRequest"/> — a distinct type only because SeasonConfiguration has no separate name in the request body per OpenAPI, kept apart here so a future Season-only field never has to be shoehorned into the League request's shape.</summary>
public sealed class UpdateSeasonConfigurationRequest
{
    [Range(1, int.MaxValue)]
    public int InitialSquadSize { get; set; }

    [Range(1, int.MaxValue)]
    public int WeeklyRosterSize { get; set; }

    [Required]
    public PositionalMinimumsDto PositionalMinimums { get; set; } = null!;

    [Required]
    public DraftTimerSecondsByTypeDto DraftTimerSecondsByType { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int SecondaryDraftSelectionsPerTeam { get; set; }

    [Range(0, int.MaxValue)]
    public int SecondaryDraftSchedulingOffsetDays { get; set; }

    [Range(0, int.MaxValue)]
    public int GameweekRosterLockOffsetBeforeKickoffMinutes { get; set; }

    [Required]
    public LeaguePointsDto LeaguePoints { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int InvitationExpirationDays { get; set; }

    [Range(0, int.MaxValue)]
    public int? ReplacementSelectionCap { get; set; }

    [Range(0, int.MaxValue)]
    public int GameweekReminderLeadTimeHours { get; set; }

    [Required]
    public string TieBreakRulesetVersion { get; set; } = null!;

    public ConfigurationValues ToValues() => new(
        InitialSquadSize,
        WeeklyRosterSize,
        PositionalMinimums.Gk,
        PositionalMinimums.Def,
        PositionalMinimums.Mid,
        PositionalMinimums.Fwd,
        DraftTimerSecondsByType.Initial,
        DraftTimerSecondsByType.Secondary,
        DraftTimerSecondsByType.Replacement,
        SecondaryDraftSelectionsPerTeam,
        SecondaryDraftSchedulingOffsetDays,
        GameweekRosterLockOffsetBeforeKickoffMinutes,
        LeaguePoints.Win,
        LeaguePoints.Draw,
        LeaguePoints.Loss,
        InvitationExpirationDays,
        ReplacementSelectionCap,
        GameweekReminderLeadTimeHours,
        TieBreakRulesetVersion);
}

public sealed class LeagueConfigurationDto
{
    public required Guid LeagueId { get; init; }
    public required int InitialSquadSize { get; init; }
    public required int WeeklyRosterSize { get; init; }
    public required PositionalMinimumsDto PositionalMinimums { get; init; }
    public required DraftTimerSecondsByTypeDto DraftTimerSecondsByType { get; init; }
    public required int SecondaryDraftSelectionsPerTeam { get; init; }
    public required int SecondaryDraftSchedulingOffsetDays { get; init; }
    public required int GameweekRosterLockOffsetBeforeKickoffMinutes { get; init; }
    public required LeaguePointsDto LeaguePoints { get; init; }
    public required int InvitationExpirationDays { get; init; }
    public int? ReplacementSelectionCap { get; init; }
    public required int GameweekReminderLeadTimeHours { get; init; }
    public required string TieBreakRulesetVersion { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public Guid? UpdatedByMembershipId { get; init; }

    public static LeagueConfigurationDto From(LeagueConfiguration configuration) => new()
    {
        LeagueId = configuration.LeagueId,
        InitialSquadSize = configuration.InitialSquadSize,
        WeeklyRosterSize = configuration.WeeklyRosterSize,
        PositionalMinimums = new PositionalMinimumsDto
        {
            Gk = configuration.PositionalMinimumGk,
            Def = configuration.PositionalMinimumDef,
            Mid = configuration.PositionalMinimumMid,
            Fwd = configuration.PositionalMinimumFwd,
        },
        DraftTimerSecondsByType = new DraftTimerSecondsByTypeDto
        {
            Initial = configuration.DraftTimerSecondsInitial,
            Secondary = configuration.DraftTimerSecondsSecondary,
            Replacement = configuration.DraftTimerSecondsReplacement,
        },
        SecondaryDraftSelectionsPerTeam = configuration.SecondaryDraftSelectionsPerTeam,
        SecondaryDraftSchedulingOffsetDays = configuration.SecondaryDraftSchedulingOffsetDays,
        GameweekRosterLockOffsetBeforeKickoffMinutes = configuration.GameweekRosterLockOffsetBeforeKickoffMinutes,
        LeaguePoints = new LeaguePointsDto
        {
            Win = configuration.LeaguePointsWin,
            Draw = configuration.LeaguePointsDraw,
            Loss = configuration.LeaguePointsLoss,
        },
        InvitationExpirationDays = configuration.InvitationExpirationDays,
        ReplacementSelectionCap = configuration.ReplacementSelectionCap,
        GameweekReminderLeadTimeHours = configuration.GameweekReminderLeadTimeHours,
        TieBreakRulesetVersion = configuration.TieBreakRulesetVersion,
        UpdatedAt = configuration.UpdatedAt,
        UpdatedByMembershipId = configuration.UpdatedByMembershipId,
    };
}

/// <summary>
/// OpenAPI's SeasonConfiguration schema is `allOf: [LeagueConfiguration, {seasonId, lockedFields}]`
/// — a shorthand for "the same numeric fields, plus these two," not a literal instruction to also
/// carry League-only fields (updatedAt/updatedByMembershipId/leagueId) SeasonConfiguration's own
/// domain model (IT-06) has no data for.
/// </summary>
public sealed class SeasonConfigurationDto
{
    public required Guid SeasonId { get; init; }
    public required int InitialSquadSize { get; init; }
    public required int WeeklyRosterSize { get; init; }
    public required PositionalMinimumsDto PositionalMinimums { get; init; }
    public required DraftTimerSecondsByTypeDto DraftTimerSecondsByType { get; init; }
    public required int SecondaryDraftSelectionsPerTeam { get; init; }
    public required int SecondaryDraftSchedulingOffsetDays { get; init; }
    public required int GameweekRosterLockOffsetBeforeKickoffMinutes { get; init; }
    public required LeaguePointsDto LeaguePoints { get; init; }
    public required int InvitationExpirationDays { get; init; }
    public int? ReplacementSelectionCap { get; init; }
    public required int GameweekReminderLeadTimeHours { get; init; }
    public required string TieBreakRulesetVersion { get; init; }
    public required IReadOnlyList<string> LockedFields { get; init; }

    public static SeasonConfigurationDto From(SeasonConfiguration configuration) => new()
    {
        SeasonId = configuration.SeasonId,
        InitialSquadSize = configuration.InitialSquadSize,
        WeeklyRosterSize = configuration.WeeklyRosterSize,
        PositionalMinimums = new PositionalMinimumsDto
        {
            Gk = configuration.PositionalMinimumGk,
            Def = configuration.PositionalMinimumDef,
            Mid = configuration.PositionalMinimumMid,
            Fwd = configuration.PositionalMinimumFwd,
        },
        DraftTimerSecondsByType = new DraftTimerSecondsByTypeDto
        {
            Initial = configuration.DraftTimerSecondsInitial,
            Secondary = configuration.DraftTimerSecondsSecondary,
            Replacement = configuration.DraftTimerSecondsReplacement,
        },
        SecondaryDraftSelectionsPerTeam = configuration.SecondaryDraftSelectionsPerTeam,
        SecondaryDraftSchedulingOffsetDays = configuration.SecondaryDraftSchedulingOffsetDays,
        GameweekRosterLockOffsetBeforeKickoffMinutes = configuration.GameweekRosterLockOffsetBeforeKickoffMinutes,
        LeaguePoints = new LeaguePointsDto
        {
            Win = configuration.LeaguePointsWin,
            Draw = configuration.LeaguePointsDraw,
            Loss = configuration.LeaguePointsLoss,
        },
        InvitationExpirationDays = configuration.InvitationExpirationDays,
        ReplacementSelectionCap = configuration.ReplacementSelectionCap,
        GameweekReminderLeadTimeHours = configuration.GameweekReminderLeadTimeHours,
        TieBreakRulesetVersion = configuration.TieBreakRulesetVersion,
        LockedFields = configuration.LockedFields,
    };
}
