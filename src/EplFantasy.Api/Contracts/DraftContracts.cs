using System.ComponentModel.DataAnnotations;
using EplFantasy.Drafts;
using EplFantasy.PlayerData;
using EplFantasy.Reporting;

namespace EplFantasy.Api.Contracts;

// IT-23 (F-005.1): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// createDraft's exact request/response OpenAPI schemas.

public sealed class CreateDraftRequest
{
    [Required]
    public string DraftType { get; set; } = null!;

    public DateTimeOffset? StartTime { get; set; }
}

public sealed class DraftDto
{
    public required Guid DraftId { get; init; }
    public required Guid SeasonId { get; init; }
    public required string DraftType { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<Guid> DraftOrder { get; init; }
    public required int CurrentRound { get; init; }
    public required int CurrentPickIndex { get; init; }
    public required int TimerSeconds { get; init; }
    public DateTimeOffset? CurrentPickDeadline { get; init; }
    public required IReadOnlyList<Guid> PendingMakeupPicks { get; init; }

    public static DraftDto From(Draft draft) => new()
    {
        DraftId = draft.DraftId,
        SeasonId = draft.SeasonId,
        DraftType = draft.DraftType.ToString(),
        Status = draft.Status.ToString(),
        DraftOrder = draft.DraftOrder,
        CurrentRound = draft.CurrentRound,
        CurrentPickIndex = draft.CurrentPickIndex,
        TimerSeconds = draft.TimerSeconds,
        CurrentPickDeadline = draft.CurrentPickDeadline,
        PendingMakeupPicks = draft.PendingMakeupPicks,
    };
}

// IT-24 (F-005.2): makeDraftPick's exact request/response OpenAPI schemas.

public sealed class MakeDraftPickRequest
{
    [Required]
    public Guid? PlayerId { get; set; }
}

public sealed class DraftSelectionDto
{
    public required Guid DraftSelectionId { get; init; }
    public required Guid DraftId { get; init; }
    public required Guid FantasyTeamId { get; init; }
    public required Guid PlayerId { get; init; }
    public required int Round { get; init; }
    public required int PickNumber { get; init; }
    public required DateTimeOffset SelectedAt { get; init; }
    public required bool IsMakeupPick { get; init; }

    public static DraftSelectionDto From(DraftSelection selection) => new()
    {
        DraftSelectionId = selection.DraftSelectionId,
        DraftId = selection.DraftId,
        FantasyTeamId = selection.FantasyTeamId,
        PlayerId = selection.PlayerId,
        Round = selection.Round,
        PickNumber = selection.PickNumber,
        SelectedAt = selection.SelectedAt,
        IsMakeupPick = selection.IsMakeupPick,
    };
}

/// <summary>IT-26 (F-005.3): extendDraftTimer's exact request OpenAPI schema — response is the same DraftDto createDraft/getDraft already use.</summary>
public sealed class ExtendDraftTimerRequest
{
    [Range(1, int.MaxValue)]
    public int AdditionalSeconds { get; set; }
}

// IT-28 (F-005.5): listDraftSelections/getDraftPlayerPool's exact response OpenAPI schemas.

public sealed class DraftSelectionPageDto
{
    public required IReadOnlyList<DraftSelectionDto> Items { get; init; }
    public string? NextCursor { get; init; }
}

public sealed class DraftPlayerPoolEntryDto
{
    public required Guid PlayerId { get; init; }
    public required string PlayerName { get; init; }
    public Guid? ClubId { get; init; }
    public required string Position { get; init; }
    public required long SeasonMinutesPlayed { get; init; }
    public required long SeasonGamesPlayed { get; init; }
    public required long SeasonFantasyPoints { get; init; }

    public static DraftPlayerPoolEntryDto From(Player player, PlayerSeasonStatistics? stats) => new()
    {
        PlayerId = player.PlayerId,
        PlayerName = player.Name,
        ClubId = player.CurrentClubId,
        Position = player.Position.ToString(),
        SeasonMinutesPlayed = stats?.MinutesPlayed ?? 0,
        SeasonGamesPlayed = stats?.GamesPlayed ?? 0,
        SeasonFantasyPoints = stats?.FantasyPoints ?? 0,
    };
}
