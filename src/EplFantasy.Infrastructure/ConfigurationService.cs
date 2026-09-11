using EplFantasy.Administration;
using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext and IAdministrativeActionRecorder, so each update and its audit row commit atomically in one SaveChangesAsync().</summary>
public sealed class ConfigurationService(
    EplFantasyDbContext dbContext,
    IAdministrativeActionRecorder administrativeActionRecorder,
    IClock clock) : IConfigurationService
{
    private static readonly Error SeasonNotFound = new("season_not_found", "No Season with that id exists in this League.");

    public async Task<LeagueConfiguration> UpdateLeagueConfigurationAsync(
        Guid leagueId,
        Guid actingMembershipId,
        ConfigurationValues values,
        CancellationToken cancellationToken = default)
    {
        var configuration = await dbContext.LeagueConfigurations.SingleAsync(c => c.LeagueId == leagueId, cancellationToken);
        var before = configuration.ToValues();

        configuration.Update(values, actingMembershipId, clock.UtcNow);

        administrativeActionRecorder.Record(
            leagueId,
            actingMembershipId,
            AdminActionType.ConfigurationChanged,
            targetEntityType: "League",
            targetEntityId: leagueId,
            beforeState: before,
            afterState: values);

        await dbContext.SaveChangesAsync(cancellationToken);

        return configuration;
    }

    public async Task<Result<SeasonConfiguration>> UpdateSeasonConfigurationAsync(
        Guid leagueId,
        Guid seasonId,
        Guid actingMembershipId,
        ConfigurationValues values,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return Result.Failure<SeasonConfiguration>(SeasonNotFound);
        }

        var configuration = await dbContext.SeasonConfigurations.SingleAsync(c => c.SeasonId == seasonId, cancellationToken);
        var before = configuration.ToValues();

        // Throws SeasonConfigurationFieldsLockedException (409) if the request changes any
        // already-locked field — deliberately not caught here; it propagates to IT-F14's
        // GlobalExceptionHandler like every other DomainException, before the audit row below is
        // ever staged.
        configuration.ApplyUpdate(values);

        administrativeActionRecorder.Record(
            leagueId,
            actingMembershipId,
            AdminActionType.ConfigurationChanged,
            targetEntityType: "Season",
            targetEntityId: seasonId,
            beforeState: before,
            afterState: values);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(configuration);
    }
}
