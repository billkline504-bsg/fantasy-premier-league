using EplFantasy.FantasyTeams;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using EplFantasy.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V008__scoring.sql. original_value/override_value are
// jsonb columns mapped as raw JSON text for now (HasColumnType("jsonb") on a string property) —
// a typed mapping (JsonDocument or a strongly-typed override payload) is a refinement for the
// feature task that actually implements score overrides (IT-37, F-008.5), not this wiring task.
// See IdentityConfigurations.cs's header comment for why every FK is declared without a
// navigation property.

public class PlayerPerformanceConfiguration : IEntityTypeConfiguration<PlayerPerformance>
{
    public void Configure(EntityTypeBuilder<PlayerPerformance> builder)
    {
        builder.ToTable("player_performances");
        builder.HasKey(x => x.PlayerPerformanceId);
        builder.HasIndex(x => new { x.GameweekId, x.PlayerId }).IsUnique();

        builder.HasOne<Gameweek>().WithMany().HasForeignKey(x => x.GameweekId);
        builder.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}

public class GameweekScoreConfiguration : IEntityTypeConfiguration<GameweekScore>
{
    public void Configure(EntityTypeBuilder<GameweekScore> builder)
    {
        builder.ToTable("gameweek_scores");
        builder.HasKey(x => x.GameweekScoreId);
        builder.HasIndex(x => new { x.FantasyTeamId, x.GameweekId }).IsUnique();

        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.FantasyTeamId);
        builder.HasOne<Gameweek>().WithMany().HasForeignKey(x => x.GameweekId);
    }
}

public class ScoreOverrideConfiguration : IEntityTypeConfiguration<ScoreOverride>
{
    public void Configure(EntityTypeBuilder<ScoreOverride> builder)
    {
        builder.ToTable("score_overrides");
        builder.HasKey(x => x.ScoreOverrideId);

        builder.Property(x => x.OriginalValueJson).HasColumnName("original_value").HasColumnType("jsonb");
        builder.Property(x => x.OverrideValueJson).HasColumnName("override_value").HasColumnType("jsonb");

        builder.HasOne<PlayerPerformance>().WithMany().HasForeignKey(x => x.PlayerPerformanceId);
        builder.HasOne<LeagueMembership>().WithMany().HasForeignKey(x => x.AdministratorMembershipId);
    }
}
