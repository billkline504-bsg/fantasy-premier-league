using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-17/IT-18/IT-19 (F-004.1/F-004.2/F-004.6): read-only EPL reference data —
/// listClubs/listPlayers/getPlayer, listGameweeks/getGameweekFixtures, and getEplTable. No write
/// route exists here by design (OpenAPI Specification v1.0 §"Player & EPL Data" declares reads
/// only): the actual Club/Player/Gameweek/Fixture/ClubStanding rows are populated by
/// IPlayerDataSyncService's upsert/recompute logic (PlayerDataSyncService — built at IT-F11 for
/// Club/Player/Gameweek/Fixture, extended at IT-19 to also recompute ClubStanding; a concrete
/// IFplDataSource — FplApiDataSource — arrives with IT-17/IT-18), never by a request through this
/// controller. Actually *triggering* that sync on a recurring schedule is not part of any of these
/// tasks either — none of their breakdown entries list a background-hosted-service line (unlike
/// ADR-012's deadline sweep, which got its own dedicated foundational task, IT-F08) — and is left
/// for later. getEplTable/getGameweekFixtures are also BR-335's "visible to any authenticated user
/// regardless of League membership" — the same plain-[Authorize], no-object-level-check shape every
/// route on this controller already uses.
/// </summary>
[ApiController]
[Route("api/v1/epl")]
[Authorize]
public class PlayerDataController(EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpGet("clubs")]
    public async Task<IActionResult> ListClubs(CancellationToken cancellationToken)
    {
        var clubs = await dbContext.Clubs.AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        return Ok(clubs.Select(ClubDto.From));
    }

    [HttpGet("players")]
    public async Task<IActionResult> ListPlayers(Guid? clubId, PlayerPosition? position, string? search, CancellationToken cancellationToken)
    {
        var query = dbContext.Players.AsNoTracking().AsQueryable();

        if (clubId is not null)
        {
            query = query.Where(p => p.CurrentClubId == clubId);
        }

        if (position is not null)
        {
            query = query.Where(p => p.Position == position);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(normalizedSearch));
        }

        var players = await query.OrderBy(p => p.Name).ToListAsync(cancellationToken);

        return Ok(players.Select(PlayerDto.From));
    }

    [HttpGet("players/{playerId:guid}")]
    public async Task<IActionResult> GetPlayer(Guid playerId, CancellationToken cancellationToken)
    {
        var player = await dbContext.Players.AsNoTracking().SingleOrDefaultAsync(p => p.PlayerId == playerId, cancellationToken);

        return player is null ? NotFound() : Ok(PlayerDto.From(player));
    }

    [HttpGet("gameweeks")]
    public async Task<IActionResult> ListGameweeks([FromQuery] string eplSeasonIdentifier, CancellationToken cancellationToken)
    {
        var gameweeks = await dbContext.Gameweeks.AsNoTracking()
            .Where(g => g.EplSeasonIdentifier == eplSeasonIdentifier)
            .OrderBy(g => g.Number)
            .ToListAsync(cancellationToken);

        return Ok(gameweeks.Select(GameweekDto.From));
    }

    [HttpGet("gameweeks/{gameweekId:guid}/fixtures")]
    public async Task<IActionResult> GetGameweekFixtures(Guid gameweekId, CancellationToken cancellationToken)
    {
        var fixtures = await dbContext.Fixtures.AsNoTracking()
            .Where(f => f.GameweekId == gameweekId)
            .OrderBy(f => f.KickoffTime)
            .ToListAsync(cancellationToken);

        return Ok(fixtures.Select(FixtureDto.From));
    }

    [HttpGet("seasons/{eplSeasonId}/table")]
    public async Task<IActionResult> GetEplTable(string eplSeasonId, CancellationToken cancellationToken)
    {
        var standings = await dbContext.ClubStandings.AsNoTracking()
            .Where(s => s.EplSeasonIdentifier == eplSeasonId)
            .OrderBy(s => s.Position)
            .ToListAsync(cancellationToken);

        return Ok(standings.Select(ClubStandingDto.From));
    }
}
