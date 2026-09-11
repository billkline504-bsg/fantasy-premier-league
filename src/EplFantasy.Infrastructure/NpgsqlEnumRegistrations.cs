using Npgsql;

namespace EplFantasy.Infrastructure;

/// <summary>
/// Every C# enum in the domain model maps to a native PostgreSQL <c>ENUM</c> type created by
/// <c>06-database-migrations/migrations/</c> (e.g. <c>user_status</c>), per the Database Migration
/// Strategy's "native PostgreSQL ENUM types" decision (§3). Npgsql requires each mapping to be
/// registered on the <see cref="NpgsqlDataSource"/> *before* it is used — there is no way to
/// discover these lazily via EF Core's model alone — so this is applied once, centrally, rather
/// than scattered per entity configuration.
///
/// The default <see cref="NpgsqlNameTranslator"/> ("PascalCase member → snake_case label") matches
/// every PG enum label exactly as written in the migrations (verified: e.g. <c>StartingXi</c> →
/// <c>starting_xi</c>, <c>OfficialFpl</c> → <c>official_fpl</c>), so no per-value override is
/// needed — this is also exercised for real against the physical schema in
/// EplFantasyDbContextTests (EplFantasy.IntegrationTests).
/// </summary>
public static class NpgsqlEnumRegistrations
{
    public static NpgsqlDataSourceBuilder MapEplFantasyEnums(this NpgsqlDataSourceBuilder builder)
    {
        builder.MapEnum<Identity.UserStatus>("user_status");

        builder.MapEnum<PlayerData.PlayerPosition>("player_position");
        builder.MapEnum<PlayerData.FixtureStatus>("fixture_status");

        builder.MapEnum<Scoring.PerformanceSource>("performance_source");

        builder.MapEnum<Leagues.LeagueStatus>("league_status");
        builder.MapEnum<Leagues.MembershipStatus>("membership_status");
        builder.MapEnum<Leagues.SeasonStatus>("season_status");
        builder.MapEnum<Leagues.InvitationStatus>("invitation_status");
        builder.MapEnum<Leagues.InvitationChannel>("invitation_channel");

        builder.MapEnum<FantasyTeams.FantasyTeamStatus>("fantasy_team_status");
        builder.MapEnum<FantasyTeams.AcquisitionType>("acquisition_type");

        builder.MapEnum<Drafts.DraftType>("draft_type");
        builder.MapEnum<Drafts.DraftStatus>("draft_status");
        builder.MapEnum<Drafts.ReplacementGrantReason>("replacement_grant_reason");

        builder.MapEnum<Rosters.RosterStatus>("roster_status");
        builder.MapEnum<Rosters.SelectionRole>("selection_role");

        builder.MapEnum<Competition.MatchResult>("match_result");

        builder.MapEnum<Administration.AdminActionType>("admin_action_type");
        builder.MapEnum<Administration.SecurityEventType>("security_event_type");

        builder.MapEnum<Notifications.NotificationEventType>("notification_event_type");
        builder.MapEnum<Notifications.NotificationChannel>("notification_channel");
        builder.MapEnum<Notifications.NotificationStatus>("notification_status");

        return builder;
    }
}
