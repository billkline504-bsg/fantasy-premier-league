using EplFantasy.Administration;
using EplFantasy.Competition;
using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure.Idempotency;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EplFantasy.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EplFantasyDbContext"/> against the given PostgreSQL connection
    /// string, with every domain enum mapped via <see cref="NpgsqlEnumRegistrations"/> and
    /// snake_case column/table naming (matching 06-database-migrations/'s hand-written schema —
    /// this DbContext maps against an already-migrated database, never generates its own schema).
    /// Also registers <see cref="IClock"/> against <see cref="SystemClock"/> (the only wiring
    /// point where the real wall clock enters the application), <see cref="IAdministrativeActionRecorder"/>
    /// — Scoped, deliberately the same lifetime as <see cref="EplFantasyDbContext"/> itself (see
    /// that recorder's own remarks on why) — and <see cref="IAuthoritativeValueResolver"/>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.MapEplFantasyEnums();
        var dataSource = dataSourceBuilder.Build();

        services.AddSingleton(dataSource);

        services.AddDbContext<EplFantasyDbContext>(options => options
            .UseNpgsql(dataSource)
            .UseSnakeCaseNamingConvention()
            // Each integration test builds its own distinct connection string/data source against
            // a disposable Testcontainers Postgres by design (test isolation) — EF Core's "you've
            // built suspiciously many internal service providers" heuristic is a false positive
            // for that pattern, not a sign of the bug it's meant to catch (see
            // EplFantasy.TestSupport.TestDbContextOptionsFactory's remarks for the full story).
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IAdministrativeActionRecorder, AdministrativeActionRecorder>();
        services.AddScoped<ISecurityEventRecorder, SecurityEventRecorder>();
        services.AddScoped<IAuthoritativeValueResolver, AuthoritativeValueResolver>();

        // IT-01: needs IAuthenticationService (registered separately by
        // AddAuthenticationInfrastructure, always called alongside this method in Program.cs) —
        // like IStandingsTieBreakContextLoader above, this is safe because plain
        // ServiceCollection.BuildServiceProvider() (every test in this codebase) never eagerly
        // validates the DI graph the way a real WebApplicationBuilder.Build() does in Development.
        services.AddScoped<IUserAccountService, UserAccountService>();

        // IT-03: needs IAdministrativeActionRecorder (registered just above) for UpdateAsync's
        // audit row.
        services.AddScoped<ILeagueService, LeagueService>();

        // IT-04.
        services.AddScoped<IInvitationService, InvitationService>();

        // IT-05.
        services.AddScoped<IMembershipService, MembershipService>();

        // IT-06.
        services.AddScoped<ISeasonService, SeasonService>();

        // IT-07.
        services.AddScoped<ISeasonGoalPredictionService, SeasonGoalPredictionService>();

        // IT-08: needs IAdministrativeActionRecorder (registered above) for its ConfigurationChanged audit row.
        services.AddScoped<IConfigurationService, ConfigurationService>();

        // IT-09.
        services.AddScoped<IProfileService, ProfileService>();

        // IT-11.
        services.AddScoped<IFantasyTeamService, FantasyTeamService>();

        // IT-14.
        services.AddScoped<IUsernameService, UsernameService>();

        // IT-15.
        services.AddScoped<IUserRetirementService, UserRetirementService>();

        // IT-57.
        services.AddScoped<IUsernameHistoryResolver, UsernameHistoryResolver>();

        // ADR-008: the tie-break pipeline is pure/stateless (Singleton-safe); only the context
        // loader touches the DbContext, so it stays Scoped.
        services.AddSingleton<IStandingsTieBreakRuleset, V1StandingsTieBreakRuleset>();
        services.AddSingleton<StandingsTieBreakPipeline>();
        services.AddScoped<IStandingsTieBreakContextLoader, StandingsTieBreakContextLoader>();

        // IT-F14 (BR-237, Architecture §9.3): backing store for [Idempotent]-decorated actions.
        services.AddMemoryCache();
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();

        // IT-17 (F-004.1, BR-289): the real, unofficial FPL API — resolved through
        // IHttpClientFactory (AddHttpClient) rather than a bare `new HttpClient()`, so pooled
        // connections/DNS rotation work the way Microsoft's own HttpClient guidance recommends.
        services.AddHttpClient<IFplDataSource, FplApiDataSource>(client =>
        {
            client.BaseAddress = new Uri("https://fantasy.premierleague.com/api/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IPlayerDataSyncService, PlayerDataSyncService>();

        // IT-39 (F-009.2): registered ahead of IGameweekScoreCalculationService below since that
        // service now calls this one as its own post-commit cascade step.
        services.AddScoped<IMatchResultCalculationService, MatchResultCalculationService>();

        // IT-33 (F-008.1): registered ahead of IPlayerPerformanceSyncService below since that
        // service now calls this one at the end of every sync.
        services.AddScoped<IGameweekScoreCalculationService, GameweekScoreCalculationService>();

        // IT-20 (F-004.3): PlayerPerformance is a Scoring-context aggregate, not PlayerData's —
        // a separate service from IPlayerDataSyncService above, but the same IFplDataSource.
        services.AddScoped<IPlayerPerformanceSyncService, PlayerPerformanceSyncService>();

        // IT-37 (F-008.5).
        services.AddScoped<IScoreOverrideService, ScoreOverrideService>();

        // IT-23 (F-005.1): the randomizer is pure/stateless (Singleton-safe, same reasoning as the
        // tie-break pipeline above); the service itself needs the DbContext, so it stays Scoped.
        services.AddSingleton<IDraftOrderRandomizer, DraftOrderRandomizer>();
        services.AddScoped<IDraftService, DraftService>();

        // IT-38 (F-009.1): reuses IDraftOrderRandomizer above, registered here since this is its
        // second consumer.
        services.AddScoped<IScheduleGenerationService, ScheduleGenerationService>();

        // IT-29 (F-007.1).
        services.AddScoped<IRosterService, RosterService>();

        // IT-42 (F-010.1): reuses IStandingsTieBreakContextLoader/StandingsTieBreakPipeline (both
        // registered above, IT-F10) to rank the Season's own freshly-tallied FantasyTeams.
        services.AddScoped<IStandingsCalculationService, StandingsCalculationService>();

        // IT-45 (F-006.1): a pure proposal calculation (SecondaryDraftScheduler, EplFantasy.Drafts)
        // wrapped with the Season's own configuration/fixture-calendar lookups.
        services.AddScoped<ISecondaryDraftSchedulingService, SecondaryDraftSchedulingService>();

        // IT-51 (F-003.6).
        services.AddScoped<ILeagueMessageService, LeagueMessageService>();

        // IT-53 (F-012.1).
        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();

        return services;
    }
}
