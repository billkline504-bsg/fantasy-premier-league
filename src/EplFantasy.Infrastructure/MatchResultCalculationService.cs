using EplFantasy.Competition;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-39/IT-40 (F-009.2/F-009.3, BR-111-BR-117): reads both sides' <c>GameweekScore.FantasyPoints</c>
/// for the given FantasyTeam's Gameweek fixture, re-derives the <see cref="HeadToHeadMatch"/>
/// result, and allocates each side's own configured League Points for it — leaves the match
/// untouched (not merely "unchanged", genuinely never queried past the initial lookup) whenever
/// there's no scheduled fixture (BR-306 bye) or the opposing FantasyTeam hasn't been scored yet
/// (F-009.2 AC4: both sides must be Locked and Scored first).
/// </summary>
public sealed class MatchResultCalculationService(EplFantasyDbContext dbContext) : IMatchResultCalculationService
{
    public async Task CalculateForFantasyTeamGameweekAsync(Guid fantasyTeamId, Guid gameweekId, CancellationToken cancellationToken = default)
    {
        var match = await dbContext.HeadToHeadMatches.SingleOrDefaultAsync(
            m => m.GameweekId == gameweekId && (m.HomeFantasyTeamId == fantasyTeamId || m.AwayFantasyTeamId == fantasyTeamId),
            cancellationToken);

        if (match is null)
        {
            return;
        }

        var homeScore = await dbContext.GameweekScores.SingleOrDefaultAsync(
            s => s.FantasyTeamId == match.HomeFantasyTeamId && s.GameweekId == gameweekId, cancellationToken);
        var awayScore = await dbContext.GameweekScores.SingleOrDefaultAsync(
            s => s.FantasyTeamId == match.AwayFantasyTeamId && s.GameweekId == gameweekId, cancellationToken);

        if (homeScore is null || awayScore is null)
        {
            return;
        }

        match.CalculateResult(homeScore.FantasyPoints, awayScore.FantasyPoints);

        // ADR-011: the Season's own locked-in configuration (BR-293), never a hard-coded 3/1/0.
        var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(
            sc => sc.SeasonId == match.SeasonId, cancellationToken);
        match.AllocateLeaguePoints(seasonConfiguration.LeaguePointsWin, seasonConfiguration.LeaguePointsDraw, seasonConfiguration.LeaguePointsLoss);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
