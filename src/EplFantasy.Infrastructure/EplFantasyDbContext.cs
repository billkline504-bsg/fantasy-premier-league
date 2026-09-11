using EplFantasy.Administration;
using EplFantasy.Competition;
using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Reporting;
using EplFantasy.Rosters;
using EplFantasy.Scoring;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>
/// The single DbContext for the modular monolith (ADR-001), mapping every table created by
/// <c>06-database-migrations/migrations/V001</c>–<c>V013</c>. This context maps against an
/// *already-migrated* schema — the hand-written SQL in that folder is the authoritative physical
/// design (Database Migration Strategy v1.0 §1); this DbContext is never used to generate or apply
/// its own EF Core migrations in normal operation. No <c>Migrations/</c> folder is committed here.
/// </summary>
public class EplFantasyDbContext(DbContextOptions<EplFantasyDbContext> options) : DbContext(options)
{
    // Identity & User (V002)
    public DbSet<User> Users => Set<User>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<UsernameHistory> UsernameHistories => Set<UsernameHistory>();
    public DbSet<ProfileIcon> ProfileIcons => Set<ProfileIcon>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    // Player & EPL Data (V003)
    public DbSet<EplSeason> EplSeasons => Set<EplSeason>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Gameweek> Gameweeks => Set<Gameweek>();
    public DbSet<Fixture> Fixtures => Set<Fixture>();
    public DbSet<ClubStanding> ClubStandings => Set<ClubStanding>();

    // League & Season (V004)
    public DbSet<League> Leagues => Set<League>();
    public DbSet<LeagueMembership> LeagueMemberships => Set<LeagueMembership>();
    public DbSet<LeagueConfiguration> LeagueConfigurations => Set<LeagueConfiguration>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<SeasonConfiguration> SeasonConfigurations => Set<SeasonConfiguration>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<LeagueMessage> LeagueMessages => Set<LeagueMessage>();

    // Fantasy Team (V005)
    public DbSet<FantasyTeam> FantasyTeams => Set<FantasyTeam>();
    public DbSet<SquadPlayer> SquadPlayers => Set<SquadPlayer>();

    // Draft Management (V006)
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<DraftSelection> DraftSelections => Set<DraftSelection>();
    public DbSet<ReplacementOpportunity> ReplacementOpportunities => Set<ReplacementOpportunity>();

    // Roster Management (V007)
    public DbSet<GameweekRoster> GameweekRosters => Set<GameweekRoster>();
    public DbSet<RosterPlayer> RosterPlayers => Set<RosterPlayer>();

    // Scoring (V008)
    public DbSet<PlayerPerformance> PlayerPerformances => Set<PlayerPerformance>();
    public DbSet<GameweekScore> GameweekScores => Set<GameweekScore>();
    public DbSet<ScoreOverride> ScoreOverrides => Set<ScoreOverride>();
    public DbSet<PlayerSeasonStatistics> PlayerSeasonStatistics => Set<PlayerSeasonStatistics>();

    // Competition (V009)
    public DbSet<HeadToHeadMatch> HeadToHeadMatches => Set<HeadToHeadMatch>();
    public DbSet<LeagueStanding> LeagueStandings => Set<LeagueStanding>();
    public DbSet<SeasonGoalPrediction> SeasonGoalPredictions => Set<SeasonGoalPrediction>();

    // Corrections & Administration (V010)
    public DbSet<AdministrativeAction> AdministrativeActions => Set<AdministrativeAction>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    // Notifications (V011)
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationRequest> NotificationRequests => Set<NotificationRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EplFantasyDbContext).Assembly);
    }
}
