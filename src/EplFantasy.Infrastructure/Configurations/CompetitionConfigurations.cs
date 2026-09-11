using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V009__competition.sql. See
// IdentityConfigurations.cs's header comment for why every FK is declared without a navigation
// property.

public class HeadToHeadMatchConfiguration : IEntityTypeConfiguration<HeadToHeadMatch>
{
    public void Configure(EntityTypeBuilder<HeadToHeadMatch> builder)
    {
        builder.ToTable("head_to_head_matches");
        builder.HasKey(x => x.MatchId);
        builder.HasIndex(x => new { x.SeasonId, x.GameweekId, x.HomeFantasyTeamId, x.AwayFantasyTeamId }).IsUnique();

        builder.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId);
        builder.HasOne<Gameweek>().WithMany().HasForeignKey(x => x.GameweekId);

        // Two independent relationships to FantasyTeam (home/away) — each needs its own distinct FK.
        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.HomeFantasyTeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.AwayFantasyTeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LeagueStandingConfiguration : IEntityTypeConfiguration<LeagueStanding>
{
    public void Configure(EntityTypeBuilder<LeagueStanding> builder)
    {
        builder.ToTable("league_standings");
        builder.HasKey(x => new { x.SeasonId, x.FantasyTeamId, x.AsOfGameweekId });

        builder.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId);
        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.FantasyTeamId);
        builder.HasOne<Gameweek>().WithMany().HasForeignKey(x => x.AsOfGameweekId);
    }
}

public class SeasonGoalPredictionConfiguration : IEntityTypeConfiguration<SeasonGoalPrediction>
{
    public void Configure(EntityTypeBuilder<SeasonGoalPrediction> builder)
    {
        builder.ToTable("season_goal_predictions");
        builder.HasKey(x => x.PredictionId);
        builder.HasIndex(x => new { x.SeasonId, x.FantasyTeamId }).IsUnique();

        builder.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId);
        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.FantasyTeamId);
    }
}
