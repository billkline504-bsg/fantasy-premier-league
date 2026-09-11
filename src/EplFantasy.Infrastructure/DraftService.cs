using EplFantasy.Administration;
using EplFantasy.Competition;
using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure;

public sealed class DraftService(
    EplFantasyDbContext dbContext,
    IDraftOrderRandomizer randomizer,
    IClock clock,
    IAdministrativeActionRecorder administrativeActionRecorder,
    ILogger<DraftService> logger) : IDraftService
{
    public async Task<Draft> CreateInitialDraftAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var season = await dbContext.Seasons.SingleAsync(s => s.SeasonId == seasonId, cancellationToken);
        if (season.Status != SeasonStatus.Setup)
        {
            throw new SeasonNotReadyForInitialDraftException();
        }

        var fantasyTeamIds = await dbContext.FantasyTeams
            .Where(t => t.SeasonId == seasonId && t.Status == FantasyTeamStatus.Active)
            .Select(t => t.FantasyTeamId)
            .ToListAsync(cancellationToken);

        if (fantasyTeamIds.Count < 2)
        {
            throw new InsufficientFantasyTeamsException();
        }

        var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId, cancellationToken);
        // BR-291/BR-293: InitialSquadSize's own "Locks At" point is the start of the Initial
        // Draft — this is that exact moment (see SeasonConfiguration.Lock's own remarks, which
        // name this task directly).
        seasonConfiguration.Lock(nameof(SeasonConfiguration.InitialSquadSize));

        var draftOrder = randomizer.Shuffle(fantasyTeamIds);
        var draft = Draft.CreateInitial(Guid.NewGuid(), seasonId, draftOrder, seasonConfiguration.DraftTimerSecondsInitial, clock.UtcNow);
        dbContext.Drafts.Add(draft);

        season.Status = SeasonStatus.DraftInProgress;

        await dbContext.SaveChangesAsync(cancellationToken);

        return draft;
    }

    public async Task<Draft> CreateSecondaryDraftAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        // "Current" standings — the latest already-computed snapshot — the same resolution
        // CompetitionController.GetStandings uses for an omitted asOfGameweekId.
        var latestAsOfGameweekId = await (
            from standing in dbContext.LeagueStandings
            join gameweek in dbContext.Gameweeks on standing.AsOfGameweekId equals gameweek.GameweekId
            where standing.SeasonId == seasonId
            orderby gameweek.Number descending
            select (Guid?)standing.AsOfGameweekId
        ).FirstOrDefaultAsync(cancellationToken);

        if (latestAsOfGameweekId is null)
        {
            throw new StandingsSnapshotUnavailableException();
        }

        // BR-056: last place picks first — LeagueStanding.Position was already assigned by the full
        // ADR-008 tie-break pipeline (IStandingsCalculationService, IT-42), so descending by it
        // alone already resolves ties correctly (BR-137) without re-running that pipeline here.
        var draftOrder = await dbContext.LeagueStandings
            .Where(s => s.SeasonId == seasonId && s.AsOfGameweekId == latestAsOfGameweekId)
            .OrderByDescending(s => s.Position)
            .Select(s => s.FantasyTeamId)
            .ToListAsync(cancellationToken);

        var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId, cancellationToken);
        // BR-291: SecondaryDraftSelectionsPerTeam's own "Locks At" point is the start of the
        // Secondary Draft — this is that exact moment, the same InitialSquadSize precedent above.
        seasonConfiguration.Lock(nameof(SeasonConfiguration.SecondaryDraftSelectionsPerTeam));

        var draft = Draft.CreateSecondary(Guid.NewGuid(), seasonId, draftOrder, seasonConfiguration.DraftTimerSecondsSecondary, clock.UtcNow);
        dbContext.Drafts.Add(draft);

        await dbContext.SaveChangesAsync(cancellationToken);

        return draft;
    }

    public async Task<DraftSelection> MakePickAsync(Guid draftId, Guid callerUserId, Guid playerId, CancellationToken cancellationToken = default)
    {
        var draft = await dbContext.Drafts.SingleAsync(d => d.DraftId == draftId, cancellationToken);
        var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == draft.SeasonId, cancellationToken);

        // The caller's own FantasyTeam for this Draft's Season — never a FantasyTeamId the request
        // itself supplies. DraftTurnOwner's own authorization check already guarantees this is the
        // team currently on the clock before this method is ever reached.
        var callerFantasyTeamId = await (
            from team in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where team.SeasonId == draft.SeasonId && membership.UserId == callerUserId
            select team.FantasyTeamId
        ).SingleAsync(cancellationToken);

        // BR-059/BR-191/BR-192: an application-level pre-check for the common (non-race) case — a
        // clear 409 rather than waiting on the database round trip below. The real guarantee against
        // a genuine race is ux_squad_players_owned itself (AP-009/AP-010), caught further down.
        var alreadyOwned = await dbContext.SquadPlayers.AnyAsync(
            sp => sp.PlayerId == playerId && sp.SeasonId == draft.SeasonId && sp.IsCurrentlyOwned,
            cancellationToken);
        if (alreadyOwned)
        {
            throw new PlayerAlreadyOwnedException();
        }

        // IT-47 (F-006.3, BR-060/BR-198): the same atomic-pick engine, but a Secondary Draft has
        // its own, separate size (BR-060, default 5) — never InitialSquadSize — and its own
        // AcquisitionType, so a later listing of a squad's acquisition history (BR-264) can still
        // tell the two apart even though both simply add to the existing squad (BR-062), never
        // replacing it.
        var (totalRounds, acquisitionType) = draft.DraftType switch
        {
            DraftType.Initial => (seasonConfiguration.InitialSquadSize, AcquisitionType.InitialDraft),
            DraftType.Secondary => (seasonConfiguration.SecondaryDraftSelectionsPerTeam, AcquisitionType.SecondaryDraft),
            _ => throw new ArgumentOutOfRangeException(nameof(draft), draft.DraftType, "MakePickAsync does not support this DraftType."),
        };

        var now = clock.UtcNow;
        var selection = draft.MakePick(callerFantasyTeamId, playerId, totalRounds, now);

        dbContext.DraftSelections.Add(selection);
        dbContext.SquadPlayers.Add(new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = callerFantasyTeamId,
            PlayerId = playerId,
            SeasonId = draft.SeasonId,
            AcquisitionType = acquisitionType,
            AcquiredAt = now,
            IsCurrentlyOwned = true,
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The rare race the pre-check above can't close: two concurrent picks for the same
            // player both passed the AnyAsync check before either committed — AP-009/AP-010's
            // real guarantee, ux_squad_players_owned, catches it here instead.
            throw new PlayerAlreadyOwnedException();
        }

        logger.LogInformation(
            "{Event}: FantasyTeam {FantasyTeamId} drafted Player {PlayerId} (Draft {DraftId}, Round {Round}, Pick {PickNumber})",
            nameof(PlayerDrafted),
            callerFantasyTeamId,
            playerId,
            draftId,
            selection.Round,
            selection.PickNumber);

        return selection;
    }

    public async Task<Draft> ExtendTimerAsync(Guid draftId, int additionalSeconds, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        var draft = await dbContext.Drafts.SingleAsync(d => d.DraftId == draftId, cancellationToken);
        var season = await dbContext.Seasons.SingleAsync(s => s.SeasonId == draft.SeasonId, cancellationToken);
        var actingMembership = await dbContext.LeagueMemberships.SingleAsync(
            m => m.LeagueId == season.LeagueId && m.UserId == actingUserId && m.IsAdministrator && m.Status == MembershipStatus.Active,
            cancellationToken);

        var before = new { draft.CurrentPickDeadline };
        draft.ExtendTimer(additionalSeconds);
        var after = new { draft.CurrentPickDeadline };

        administrativeActionRecorder.Record(
            season.LeagueId,
            actingMembership.LeagueMembershipId,
            AdminActionType.DraftTimerExtended,
            targetEntityType: "Draft",
            targetEntityId: draft.DraftId,
            beforeState: before,
            afterState: after);

        await dbContext.SaveChangesAsync(cancellationToken);

        return draft;
    }

    public async Task<ReplacementOpportunity> MakeReplacementPickAsync(Guid fantasyTeamId, Guid replacementOpportunityId, Guid playerId, CancellationToken cancellationToken = default)
    {
        // Scoped to fantasyTeamId directly, the same defense-in-depth every route-scoped service
        // method in this codebase applies regardless of the API layer's own authorization check
        // (FantasyTeamOwner) — an opportunity granted to a different FantasyTeam simply doesn't
        // exist from this call's own point of view.
        var opportunity = await dbContext.ReplacementOpportunities.SingleAsync(
            o => o.ReplacementOpportunityId == replacementOpportunityId && o.FantasyTeamId == fantasyTeamId, cancellationToken);

        var fantasyTeam = await dbContext.FantasyTeams.SingleAsync(t => t.FantasyTeamId == fantasyTeamId, cancellationToken);

        // BR-261/BR-262: replacement-eligible does not mean unowned — only a genuinely unowned
        // player (the original FantasyTeam has actually released them) is selectable.
        var alreadyOwned = await dbContext.SquadPlayers.AnyAsync(
            sp => sp.PlayerId == playerId && sp.SeasonId == fantasyTeam.SeasonId && sp.IsCurrentlyOwned,
            cancellationToken);
        if (alreadyOwned)
        {
            throw new PlayerAlreadyOwnedException();
        }

        var now = clock.UtcNow;
        opportunity.MarkSpent(now); // throws ReplacementOpportunityAlreadySpentException if already spent.

        dbContext.SquadPlayers.Add(new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamId,
            PlayerId = playerId,
            SeasonId = fantasyTeam.SeasonId,
            AcquisitionType = AcquisitionType.Replacement,
            AcquiredAt = now,
            IsCurrentlyOwned = true,
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same rare race MakePickAsync guards against: two concurrent replacement picks
            // for the same player, caught by ux_squad_players_owned rather than the pre-check above.
            throw new PlayerAlreadyOwnedException();
        }

        logger.LogInformation(
            "{Event}: FantasyTeam {FantasyTeamId} spent ReplacementOpportunity {ReplacementOpportunityId} on Player {PlayerId}",
            nameof(PlayerDrafted),
            fantasyTeamId,
            replacementOpportunityId,
            playerId);

        return opportunity;
    }

    public async Task<ReplacementOpportunity?> DeclareSeasonEndingInjuryAsync(Guid leagueId, Guid squadPlayerId, string? reason, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        var squadPlayer = await dbContext.SquadPlayers.SingleAsync(sp => sp.SquadPlayerId == squadPlayerId, cancellationToken);
        if (squadPlayer.ReplacementEligibleAt is not null)
        {
            throw new SquadPlayerAlreadyReplacementEligibleException();
        }

        var fantasyTeam = await dbContext.FantasyTeams.SingleAsync(t => t.FantasyTeamId == squadPlayer.FantasyTeamId, cancellationToken);
        var actingMembership = await dbContext.LeagueMemberships.SingleAsync(
            m => m.LeagueId == leagueId && m.UserId == actingUserId && m.IsAdministrator && m.Status == MembershipStatus.Active,
            cancellationToken);
        var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == fantasyTeam.SeasonId, cancellationToken);

        var now = clock.UtcNow;
        var before = new { squadPlayer.ReplacementEligibleAt };
        squadPlayer.ReplacementEligibleAt = now;
        var after = new { squadPlayer.ReplacementEligibleAt };

        // BR-287: the same shared policy IT-21's automatic EPL-exit path already uses — eligibility
        // itself is marked above regardless, but no further opportunity is granted once this
        // FantasyTeam's own cap (spent or not) has been reached.
        ReplacementOpportunity? opportunity = null;
        var grantedSoFar = await dbContext.ReplacementOpportunities.CountAsync(ro => ro.FantasyTeamId == fantasyTeam.FantasyTeamId, cancellationToken);
        if (ReplacementOpportunityPolicy.CanGrantAnotherOpportunity(seasonConfiguration.ReplacementSelectionCap, grantedSoFar))
        {
            opportunity = new ReplacementOpportunity
            {
                ReplacementOpportunityId = Guid.NewGuid(),
                FantasyTeamId = fantasyTeam.FantasyTeamId,
                SourcePlayerId = squadPlayer.PlayerId,
                GrantedAt = now,
                GrantReason = ReplacementGrantReason.SeasonEndingInjury,
                SpentAt = null,
            };
            dbContext.ReplacementOpportunities.Add(opportunity);
        }

        // Unlike IT-21's system-generated (null actingMembershipId) grants, this is always a real,
        // resolved Administrator membership — BR-068's own "the League Administrator determines...".
        administrativeActionRecorder.Record(
            leagueId,
            actingMembership.LeagueMembershipId,
            AdminActionType.SeasonEndingInjuryDeclared,
            targetEntityType: "SquadPlayer",
            targetEntityId: squadPlayerId,
            beforeState: before,
            afterState: after,
            reason: reason);

        await dbContext.SaveChangesAsync(cancellationToken);

        return opportunity;
    }
}
