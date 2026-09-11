using EplFantasy.Drafts;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-27 (F-005.4, BR-282): ADR-012/IT-F08's sweep, applied to the Draft aggregate. Every tick,
/// finds every InProgress Draft whose CurrentPickDeadline has passed (i.e. the League Administrator
/// did not extend it, IT-26) and skips its current pick, queuing — or re-queuing, AC4 — the
/// on-the-clock FantasyTeam for a makeup pick. Only Initial Drafts exist as of this task —
/// Secondary/Replacement Drafts (F-006.x) are a future feature's concern, the same "not yet built"
/// scope DraftService.CreateInitialDraftAsync's own remarks describe — so totalRounds is always
/// SeasonConfiguration.InitialSquadSize, the same source DraftService.MakePickAsync already uses.
/// </summary>
public sealed class DraftPickTimeoutSweepHandler(
    EplFantasyDbContext dbContext,
    IClock clock,
    ILogger<DraftPickTimeoutSweepHandler> logger) : IDeadlineSweepHandler
{
    public string Name => "DraftPickTimeout";

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var overdueDrafts = await dbContext.Drafts
            .Where(d => d.Status == DraftStatus.InProgress && d.CurrentPickDeadline != null && d.CurrentPickDeadline <= now)
            .ToListAsync(cancellationToken);

        if (overdueDrafts.Count == 0)
        {
            return;
        }

        foreach (var draft in overdueDrafts)
        {
            var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(
                sc => sc.SeasonId == draft.SeasonId, cancellationToken);

            var skipped = draft.SkipCurrentPick(seasonConfiguration.InitialSquadSize, now);

            logger.LogInformation(
                "{Event}: FantasyTeam {FantasyTeamId} was skipped for Draft {DraftId} (IsRequeue: {IsRequeue})",
                nameof(DraftPickSkipped),
                skipped.FantasyTeamId,
                skipped.DraftId,
                skipped.IsRequeue);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
