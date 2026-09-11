using EplFantasy.Infrastructure;
using EplFantasy.Rosters;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Contracts;

/// <summary>
/// IT-29's original GameweekRosterDto assembly (BR-336/BR-337: joins each roster player's
/// Player.CurrentClubId against this Gameweek's own Fixture rows) — factored out of
/// RosterController at IT-32 so its owner-facing endpoints and AdminRosterController's
/// correctRoster share one implementation instead of two copies drifting apart.
/// </summary>
public static class GameweekRosterDtoFactory
{
    public static async Task<GameweekRosterDto> BuildAsync(EplFantasyDbContext dbContext, GameweekRoster roster, CancellationToken cancellationToken)
    {
        var fixtures = await dbContext.Fixtures.AsNoTracking()
            .Where(f => f.GameweekId == roster.GameweekId)
            .OrderBy(f => f.KickoffTime)
            .ToListAsync(cancellationToken);

        var opponentByClubId = new Dictionary<Guid, (Guid OpponentClubId, bool IsHome)>();
        foreach (var fixture in fixtures)
        {
            opponentByClubId[fixture.HomeClubId] = (fixture.AwayClubId, true);
            opponentByClubId[fixture.AwayClubId] = (fixture.HomeClubId, false);
        }

        var playerIds = roster.Players.Select(p => p.PlayerId).ToList();
        var clubIdByPlayerId = await dbContext.Players.AsNoTracking()
            .Where(p => playerIds.Contains(p.PlayerId))
            .ToDictionaryAsync(p => p.PlayerId, p => p.CurrentClubId, cancellationToken);

        var playerDtos = roster.Players.Select(rosterPlayer =>
        {
            var clubId = clubIdByPlayerId.GetValueOrDefault(rosterPlayer.PlayerId);
            var opponent = clubId is { } ownClubId && opponentByClubId.TryGetValue(ownClubId, out var found) ? found : ((Guid OpponentClubId, bool IsHome)?)null;

            return RosterPlayerDto.From(rosterPlayer, opponent?.OpponentClubId, opponent?.IsHome);
        }).ToList();

        return new GameweekRosterDto
        {
            GameweekRosterId = roster.GameweekRosterId,
            FantasyTeamId = roster.FantasyTeamId,
            GameweekId = roster.GameweekId,
            Status = roster.Status.ToString(),
            SubmittedAt = roster.SubmittedAt,
            LockedAt = roster.LockedAt,
            CaptainPlayerId = roster.CaptainPlayerId,
            Players = playerDtos,
            GameweekFixtures = fixtures.Select(FixtureDto.From).ToList(),
        };
    }
}
