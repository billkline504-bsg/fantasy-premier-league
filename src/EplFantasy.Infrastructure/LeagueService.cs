using EplFantasy.Administration;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext and IAdministrativeActionRecorder, so UpdateAsync's domain change and its audit row commit atomically in one SaveChangesAsync().</summary>
public sealed class LeagueService(
    EplFantasyDbContext dbContext,
    IAdministrativeActionRecorder administrativeActionRecorder,
    IClock clock) : ILeagueService
{
    public async Task<League> CreateAsync(Guid creatorUserId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var leagueId = Guid.NewGuid();
        var creatorMembershipId = Guid.NewGuid();

        var league = League.Create(leagueId, creatorMembershipId, name, description, now);
        var membership = LeagueMembership.CreateFoundingAdministrator(creatorMembershipId, leagueId, creatorUserId, now);
        var configuration = LeagueConfiguration.CreateDefault(leagueId, now);

        // All three rows insert in one SaveChangesAsync(); the leagues -> league_memberships FK is
        // DEFERRABLE INITIALLY DEFERRED (V004), validated at COMMIT rather than per-INSERT — see
        // LeagueConfigurations.cs's header comment for why EF's own model deliberately declares no
        // navigation for this one relationship. league_configurations -> leagues is a normal,
        // one-directional FK (declared in EF), so it needs no such special handling.
        dbContext.Leagues.Add(league);
        dbContext.LeagueMemberships.Add(membership);
        dbContext.LeagueConfigurations.Add(configuration);
        // IT-53 (F-012.1, BR-338): every LeagueMembership — the founding one included — gets its
        // own full set of disabled-by-default notification preference rows the instant it exists.
        dbContext.NotificationPreferences.AddRange(NotificationPreferenceSeeder.SeedFor(creatorMembershipId));
        await dbContext.SaveChangesAsync(cancellationToken);

        return league;
    }

    public async Task<League> UpdateAsync(
        Guid leagueId,
        Guid actingUserId,
        string? name,
        string? description,
        LeagueStatus? status,
        CancellationToken cancellationToken = default)
    {
        var league = await dbContext.Leagues.SingleAsync(l => l.LeagueId == leagueId, cancellationToken);
        var actingMembership = await dbContext.LeagueMemberships.SingleAsync(
            m => m.LeagueId == leagueId && m.UserId == actingUserId && m.IsAdministrator && m.Status == MembershipStatus.Active,
            cancellationToken);

        var before = new { league.Name, league.Description, Status = league.Status.ToString() };
        league.UpdateDetails(name, description, status);
        var after = new { league.Name, league.Description, Status = league.Status.ToString() };

        administrativeActionRecorder.Record(
            leagueId,
            actingMembership.LeagueMembershipId,
            AdminActionType.Other,
            targetEntityType: "League",
            targetEntityId: leagueId,
            beforeState: before,
            afterState: after);

        await dbContext.SaveChangesAsync(cancellationToken);

        return league;
    }
}
