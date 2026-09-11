using EplFantasy.Administration;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

public sealed class RosterService(
    EplFantasyDbContext dbContext,
    IClock clock,
    IAdministrativeActionRecorder administrativeActionRecorder) : IRosterService
{
    public async Task<GameweekRoster> SubmitAsync(
        Guid fantasyTeamId,
        Guid gameweekId,
        IReadOnlyList<Guid> playerIds,
        Guid? captainPlayerId,
        uint? ifMatchXmin,
        CancellationToken cancellationToken = default)
    {
        var fantasyTeam = await dbContext.FantasyTeams.SingleAsync(t => t.FantasyTeamId == fantasyTeamId, cancellationToken);
        var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == fantasyTeam.SeasonId, cancellationToken);

        var roster = await dbContext.GameweekRosters.Include(r => r.Players)
            .SingleOrDefaultAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId, cancellationToken);

        // Architecture §8.3: a manual precondition check against the caller's own claimed version —
        // distinct from (and in addition to) EF's own automatic xmin concurrency check on
        // SaveChangesAsync below, which only protects against a write racing *this* request's own
        // load, not staleness relative to an earlier GET the caller is acting on.
        if (ifMatchXmin is not null)
        {
            var currentXmin = roster is null ? (uint?)null : (uint)dbContext.Entry(roster).Property("xmin").CurrentValue!;
            if (currentXmin != ifMatchXmin)
            {
                throw new RosterConcurrencyConflictException();
            }
        }

        if (roster is null)
        {
            roster = new GameweekRoster { GameweekRosterId = Guid.NewGuid(), FantasyTeamId = fantasyTeamId, GameweekId = gameweekId };
            dbContext.GameweekRosters.Add(roster);
        }

        // BR-194: every submitted player must be a currently-owned SquadPlayer of this FantasyTeam —
        // data GameweekRoster.Submit can't see (EplFantasy.Rosters has no reference to
        // EplFantasy.FantasyTeams), so it's checked here, before Submit is ever called.
        await EnsureAllCurrentlyOwnedOrThrowAsync(fantasyTeamId, playerIds, "submitted", cancellationToken);

        // BR-299: the FantasyTeam's very first roster submission of the Season requires an on-file
        // SeasonGoalPrediction (F-010.3) — checked against every *other* GameweekRoster this
        // FantasyTeam has ever actually submitted, not just this Gameweek's (a resubmission of the
        // same first Gameweek must not re-trigger this check against itself).
        var hasEverSubmittedARoster = await dbContext.GameweekRosters
            .AnyAsync(r => r.FantasyTeamId == fantasyTeamId && r.SubmittedAt != null && r.GameweekId != gameweekId, cancellationToken);
        if (!hasEverSubmittedARoster && roster.SubmittedAt is null)
        {
            var hasPrediction = await dbContext.SeasonGoalPredictions
                .AnyAsync(p => p.FantasyTeamId == fantasyTeamId, cancellationToken);
            if (!hasPrediction)
            {
                throw new SeasonGoalPredictionRequiredException();
            }
        }

        var actualCounts = await ComputeActualPositionCountsAsync(playerIds, cancellationToken);
        var minimums = PositionalMinimumsOf(seasonConfiguration);

        roster.Submit(playerIds, captainPlayerId, seasonConfiguration.WeeklyRosterSize, minimums, actualCounts, clock.UtcNow);

        await dbContext.SaveChangesAsync(cancellationToken);

        return roster;
    }

    public async Task<GameweekRoster> SetCaptainAsync(
        Guid fantasyTeamId,
        Guid gameweekId,
        Guid captainPlayerId,
        CancellationToken cancellationToken = default)
    {
        var roster = await dbContext.GameweekRosters.Include(r => r.Players)
            .SingleOrDefaultAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId, cancellationToken);

        if (roster is null)
        {
            // No roster row exists yet for this FantasyTeam/Gameweek, so captainPlayerId can't
            // possibly be "one of the roster's selected players" (BR-046) — the same rejection
            // GameweekRoster.SetCaptain itself throws for a player outside an existing roster.
            throw new InvalidRosterCompositionException(["The designated Captain must be one of the roster's selected players."]);
        }

        roster.SetCaptain(captainPlayerId);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await ExecuteCaptainOnlyUpdateAsync(roster, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return roster;
    }

    public async Task<GameweekRoster> CorrectAsync(
        Guid gameweekRosterId,
        IReadOnlyList<Guid>? playerIds,
        Guid? captainPlayerId,
        string reason,
        Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        var roster = await dbContext.GameweekRosters.Include(r => r.Players)
            .SingleAsync(r => r.GameweekRosterId == gameweekRosterId, cancellationToken);
        var fantasyTeam = await dbContext.FantasyTeams.SingleAsync(t => t.FantasyTeamId == roster.FantasyTeamId, cancellationToken);
        var leagueId = await dbContext.LeagueMemberships
            .Where(m => m.LeagueMembershipId == fantasyTeam.LeagueMembershipId)
            .Select(m => m.LeagueId)
            .SingleAsync(cancellationToken);

        // DraftLeagueAdministrator's own controller-layer authorization check already guarantees
        // the caller is this League's Administrator before this method is ever reached — resolved
        // again here only to know *which* LeagueMembership to attribute the AdministrativeAction to.
        var actingMembership = await dbContext.LeagueMemberships.SingleAsync(
            m => m.LeagueId == leagueId && m.UserId == actingUserId && m.IsAdministrator && m.Status == MembershipStatus.Active,
            cancellationToken);

        var before = new { PlayerIds = roster.Players.Select(p => p.PlayerId).OrderBy(id => id).ToList(), roster.CaptainPlayerId };
        var isCaptainOnlyCorrection = playerIds is null;

        if (playerIds is not null)
        {
            // BR-194: the same currently-owned-SquadPlayer ownership check SubmitAsync performs —
            // an Administrator's correction may only assign players actually on this FantasyTeam's
            // squad, never an arbitrary PlayerId.
            await EnsureAllCurrentlyOwnedOrThrowAsync(fantasyTeam.FantasyTeamId, playerIds, "corrected", cancellationToken);

            var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == fantasyTeam.SeasonId, cancellationToken);
            var actualCounts = await ComputeActualPositionCountsAsync(playerIds, cancellationToken);
            var minimums = PositionalMinimumsOf(seasonConfiguration);

            roster.Correct(playerIds, captainPlayerId, seasonConfiguration.WeeklyRosterSize, minimums, actualCounts);
        }
        else
        {
            roster.CorrectCaptain(captainPlayerId);
        }

        var after = new { PlayerIds = roster.Players.Select(p => p.PlayerId).OrderBy(id => id).ToList(), roster.CaptainPlayerId };

        administrativeActionRecorder.Record(
            leagueId,
            actingMembership.LeagueMembershipId,
            AdminActionType.RosterCorrection,
            targetEntityType: "GameweekRoster",
            targetEntityId: roster.GameweekRosterId,
            beforeState: before,
            afterState: after,
            reason: reason);

        // A full player-list replacement (roster.Correct, via ReplacePlayers) deletes every old
        // RosterPlayer row and inserts fresh ones — safe as an ordinary tracked SaveChangesAsync,
        // the same delete-then-insert Submit's own resubmission path already relies on. A
        // Captain-only correction (roster.CorrectCaptain) needs the same explicit-transaction bulk
        // update SetCaptainAsync uses, for the same ux_roster_players_one_captain reason.
        if (isCaptainOnlyCorrection)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await ExecuteCaptainOnlyUpdateAsync(roster, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return roster;
    }

    /// <summary>
    /// Persists a Captain-only reassignment (SetCaptain/CorrectCaptain — player selection itself is
    /// unchanged), bypassing the tracked entities' own pending IsCaptain changes.
    /// <c>ux_roster_players_one_captain</c> (V007) is a plain, non-deferrable partial unique index
    /// — Postgres has no deferrable-partial-unique-index mechanism, and (confirmed the hard way
    /// against a real instance) checks it per row as each row is processed, even within a single
    /// multi-row UPDATE statement — so a statement that both clears the old Captain's row and sets
    /// a different row true can still violate the index if Postgres happens to process the "set
    /// true" row before the "set false" one, regardless of how many statements that spans. The only
    /// state that's unconditionally valid at every intermediate point is "no row true yet" — so this
    /// clears every row for this roster first (one statement, always safe), then, in a second,
    /// separate statement, sets only the new Captain's row true (also always safe, since nothing
    /// else can possibly still be true once the first statement has committed its own effect).
    /// Detaches this roster's own RosterPlayer entries from the change tracker afterward — the
    /// database has already been updated directly — so the caller's own still-necessary
    /// SaveChangesAsync (for GameweekRoster.CaptainPlayerId, and any AdministrativeAction) doesn't
    /// also try to write the same rows again. The caller is responsible for wrapping this call and
    /// that later SaveChangesAsync in one explicit transaction, since Postgres gives no way to make
    /// this particular constraint deferrable instead.
    /// </summary>
    private async Task ExecuteCaptainOnlyUpdateAsync(GameweekRoster roster, CancellationToken cancellationToken)
    {
        await dbContext.RosterPlayers
            .Where(p => p.GameweekRosterId == roster.GameweekRosterId && p.IsCaptain)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.IsCaptain, false), cancellationToken);

        if (roster.CaptainPlayerId is not null)
        {
            await dbContext.RosterPlayers
                .Where(p => p.GameweekRosterId == roster.GameweekRosterId && p.PlayerId == roster.CaptainPlayerId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.IsCaptain, true), cancellationToken);
        }

        // entry.State = EntityState.Unchanged alone would revert CurrentValues back to
        // OriginalValues (that's how EF resolves "Modified but now Unchanged" — it's a rollback,
        // not an accept) — wiping out the in-memory IsCaptain flips roster.SetCaptain/CorrectCaptain
        // already applied, even though the database itself now holds the correct value from the
        // bulk statements above. Syncing OriginalValues to the current (already-correct) values
        // first makes Unchanged mean what the caller actually wants: "matches the database now,
        // don't write it again."
        foreach (var entry in dbContext.ChangeTracker.Entries<RosterPlayer>().Where(e => e.Entity.GameweekRosterId == roster.GameweekRosterId))
        {
            entry.OriginalValues.SetValues(entry.CurrentValues);
            entry.State = EntityState.Unchanged;
        }
    }

    private async Task EnsureAllCurrentlyOwnedOrThrowAsync(Guid fantasyTeamId, IReadOnlyList<Guid> playerIds, string verb, CancellationToken cancellationToken)
    {
        var ownedPlayerIds = await dbContext.SquadPlayers
            .Where(sp => sp.FantasyTeamId == fantasyTeamId && sp.IsCurrentlyOwned)
            .Select(sp => sp.PlayerId)
            .ToListAsync(cancellationToken);
        var notOwnedCount = playerIds.Count(id => !ownedPlayerIds.Contains(id));
        if (notOwnedCount > 0)
        {
            throw new InvalidRosterCompositionException([
                $"{notOwnedCount} {verb} player(s) are not currently-owned SquadPlayers of this FantasyTeam."
            ]);
        }
    }

    private async Task<RosterPositionCounts> ComputeActualPositionCountsAsync(IReadOnlyList<Guid> playerIds, CancellationToken cancellationToken)
    {
        var positionByPlayerId = await dbContext.Players
            .Where(p => playerIds.Contains(p.PlayerId))
            .ToDictionaryAsync(p => p.PlayerId, p => p.Position, cancellationToken);

        return new RosterPositionCounts(
            Goalkeepers: playerIds.Count(id => positionByPlayerId.GetValueOrDefault(id) == PlayerPosition.Gk),
            Defenders: playerIds.Count(id => positionByPlayerId.GetValueOrDefault(id) == PlayerPosition.Def),
            Midfielders: playerIds.Count(id => positionByPlayerId.GetValueOrDefault(id) == PlayerPosition.Mid),
            Forwards: playerIds.Count(id => positionByPlayerId.GetValueOrDefault(id) == PlayerPosition.Fwd));
    }

    private static RosterPositionCounts PositionalMinimumsOf(SeasonConfiguration seasonConfiguration) => new(
        seasonConfiguration.PositionalMinimumGk,
        seasonConfiguration.PositionalMinimumDef,
        seasonConfiguration.PositionalMinimumMid,
        seasonConfiguration.PositionalMinimumFwd);
}
