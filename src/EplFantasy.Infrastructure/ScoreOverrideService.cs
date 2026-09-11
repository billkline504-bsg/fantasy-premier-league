using System.Text.Json;
using EplFantasy.Administration;
using EplFantasy.Leagues;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-37 (F-008.5): createScoreOverride/undoScoreOverride's application service.
/// <see cref="IScoreOverrideService"/>'s own remarks cover why <see cref="CreateAsync"/> takes a
/// <c>leagueId</c> at all, despite the entity it corrects (PlayerPerformance) being platform-level.
/// </summary>
public sealed class ScoreOverrideService(
    EplFantasyDbContext dbContext,
    IAdministrativeActionRecorder administrativeActionRecorder,
    IGameweekScoreCalculationService gameweekScoreCalculationService,
    IClock clock) : IScoreOverrideService
{
    public async Task<ScoreOverride> CreateAsync(
        Guid playerPerformanceId,
        Guid leagueId,
        IReadOnlyDictionary<string, int> overrideValue,
        string? reason,
        Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        var performance = await dbContext.PlayerPerformances.SingleAsync(p => p.PlayerPerformanceId == playerPerformanceId, cancellationToken);

        // AdminRosterController's own precedent: the controller-layer authorization check already
        // guarantees the caller administers leagueId before this method is ever reached — resolved
        // again here only to know *which* LeagueMembership to attribute the ScoreOverride/
        // AdministrativeAction to.
        var actingMembership = await dbContext.LeagueMemberships.SingleAsync(
            m => m.LeagueId == leagueId && m.UserId == actingUserId && m.IsAdministrator && m.Status == MembershipStatus.Active,
            cancellationToken);

        var originalValue = ExtractCurrentValues(performance, overrideValue.Keys);

        var scoreOverride = ScoreOverride.Create(
            Guid.NewGuid(), playerPerformanceId, actingMembership.LeagueMembershipId,
            JsonSerializer.Serialize(originalValue), JsonSerializer.Serialize(overrideValue), reason, clock.UtcNow);
        dbContext.ScoreOverrides.Add(scoreOverride);

        administrativeActionRecorder.Record(
            leagueId,
            actingMembership.LeagueMembershipId,
            AdminActionType.ScoreOverride,
            targetEntityType: "PlayerPerformance",
            targetEntityId: playerPerformanceId,
            beforeState: originalValue,
            afterState: overrideValue,
            reason: reason);

        // The override and its own audit row commit together (IAdministrativeActionRecorder's own
        // remarks); the recalculation cascade below is a separate, subsequent step — it needs the
        // override just committed above to actually be visible to IAuthoritativeValueResolver's own
        // query, which a same-DbContext in-memory Add() alone would not be.
        await dbContext.SaveChangesAsync(cancellationToken);

        await gameweekScoreCalculationService.RecalculateForPlayerPerformanceAsync(playerPerformanceId, cancellationToken);

        return scoreOverride;
    }

    public async Task<ScoreOverride> UndoAsync(Guid scoreOverrideId, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        var scoreOverride = await dbContext.ScoreOverrides.SingleAsync(o => o.ScoreOverrideId == scoreOverrideId, cancellationToken);

        // Unlike CreateAsync, "the override's League" is unambiguous here — it's whichever League
        // the override was originally created under.
        var leagueId = await dbContext.LeagueMemberships
            .Where(m => m.LeagueMembershipId == scoreOverride.AdministratorMembershipId)
            .Select(m => m.LeagueId)
            .SingleAsync(cancellationToken);

        var actingMembership = await dbContext.LeagueMemberships.SingleAsync(
            m => m.LeagueId == leagueId && m.UserId == actingUserId && m.IsAdministrator && m.Status == MembershipStatus.Active,
            cancellationToken);

        scoreOverride.Undo(clock.UtcNow);

        administrativeActionRecorder.Record(
            leagueId,
            actingMembership.LeagueMembershipId,
            AdminActionType.ScoreOverrideUndo,
            targetEntityType: "PlayerPerformance",
            targetEntityId: scoreOverride.PlayerPerformanceId,
            beforeState: new { scoreOverride.IsActive },
            afterState: new { IsActive = false },
            reason: null);

        await dbContext.SaveChangesAsync(cancellationToken);

        await gameweekScoreCalculationService.RecalculateForPlayerPerformanceAsync(scoreOverride.PlayerPerformanceId, cancellationToken);

        return scoreOverride;
    }

    /// <summary>BR-145: snapshots whichever PlayerPerformance field(s) this override is about to correct, by name, before applying it.</summary>
    private static Dictionary<string, int> ExtractCurrentValues(PlayerPerformance performance, IEnumerable<string> keys)
    {
        var result = new Dictionary<string, int>();
        foreach (var key in keys)
        {
            result[key] = key switch
            {
                "fantasyPoints" => performance.FantasyPoints,
                "goals" => performance.Goals,
                "goalsConceded" => performance.GoalsConceded,
                "ownGoals" => performance.OwnGoals,
                _ => 0,
            };
        }

        return result;
    }
}
