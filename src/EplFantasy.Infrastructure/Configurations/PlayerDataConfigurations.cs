using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V003__player_reference_data.sql. See
// IdentityConfigurations.cs's header comment for why every FK is declared without a navigation
// property.

public class EplSeasonConfiguration : IEntityTypeConfiguration<EplSeason>
{
    public void Configure(EntityTypeBuilder<EplSeason> builder)
    {
        builder.ToTable("epl_seasons");
        builder.HasKey(x => x.EplSeasonIdentifier);
    }
}

public class ClubConfiguration : IEntityTypeConfiguration<Club>
{
    public void Configure(EntityTypeBuilder<Club> builder)
    {
        builder.ToTable("clubs");
        builder.HasKey(x => x.ClubId);
        builder.HasIndex(x => x.EplClubId).IsUnique();
    }
}

public class PlayerConfiguration : IEntityTypeConfiguration<Player>
{
    public void Configure(EntityTypeBuilder<Player> builder)
    {
        builder.ToTable("players");
        builder.HasKey(x => x.PlayerId);
        builder.HasIndex(x => x.EplPlayerId).IsUnique();

        builder.HasOne<Club>().WithMany().HasForeignKey(x => x.CurrentClubId);
    }
}

public class GameweekConfiguration : IEntityTypeConfiguration<Gameweek>
{
    public void Configure(EntityTypeBuilder<Gameweek> builder)
    {
        builder.ToTable("gameweeks");
        builder.HasKey(x => x.GameweekId);
        builder.HasIndex(x => new { x.EplSeasonIdentifier, x.Number }).IsUnique();

        builder.HasOne<EplSeason>().WithMany().HasForeignKey(x => x.EplSeasonIdentifier);
    }
}

public class FixtureConfiguration : IEntityTypeConfiguration<Fixture>
{
    public void Configure(EntityTypeBuilder<Fixture> builder)
    {
        builder.ToTable("fixtures");
        builder.HasKey(x => x.FixtureId);
        builder.HasIndex(x => x.EplFixtureId).IsUnique();

        builder.HasOne<Gameweek>().WithMany().HasForeignKey(x => x.GameweekId);

        // Two independent relationships to Club (home/away) — each needs its own distinct FK.
        builder.HasOne<Club>().WithMany().HasForeignKey(x => x.HomeClubId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Club>().WithMany().HasForeignKey(x => x.AwayClubId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ClubStandingConfiguration : IEntityTypeConfiguration<ClubStanding>
{
    public void Configure(EntityTypeBuilder<ClubStanding> builder)
    {
        builder.ToTable("club_standings");
        builder.HasKey(x => new { x.EplSeasonIdentifier, x.ClubId });

        builder.HasOne<EplSeason>().WithMany().HasForeignKey(x => x.EplSeasonIdentifier);
        builder.HasOne<Club>().WithMany().HasForeignKey(x => x.ClubId);

        // Database-generated (GENERATED ALWAYS AS (goals_for - goals_against) STORED) — EF must
        // never attempt to write this column itself.
        builder.Property(x => x.GoalDifference)
            .HasComputedColumnSql("goals_for - goals_against", stored: true)
            .ValueGeneratedOnAddOrUpdate();
    }
}
