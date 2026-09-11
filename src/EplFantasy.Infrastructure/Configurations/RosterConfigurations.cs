using EplFantasy.FantasyTeams;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V007__roster.sql. Note trg_enforce_gameweek_roster_size
// (the deferred constraint trigger enforcing BR-279's positional-minimum-adjacent size check) is
// pure database behavior with nothing for EF Core to configure — it fires regardless of which
// client wrote the rows. See IdentityConfigurations.cs's header comment for why every FK is
// declared without a navigation property (except GameweekRoster.Players, which — like
// Draft.Selections — genuinely is a within-aggregate child collection).

public class GameweekRosterConfiguration : IEntityTypeConfiguration<GameweekRoster>
{
    public void Configure(EntityTypeBuilder<GameweekRoster> builder)
    {
        builder.ToTable("gameweek_rosters");
        builder.HasKey(x => x.GameweekRosterId);
        builder.HasIndex(x => new { x.FantasyTeamId, x.GameweekId }).IsUnique();

        // Architecture §8.3 / Database Migration Strategy v1.0 §3: PostgreSQL's built-in xmin
        // system column as the optimistic-concurrency token, as a shadow property (no CLR
        // property needed on GameweekRoster itself) — the modern replacement for Npgsql's now-
        // obsolete UseXminAsConcurrencyToken() helper.
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.FantasyTeamId);
        builder.HasOne<Gameweek>().WithMany().HasForeignKey(x => x.GameweekId);
        builder.HasOne<Player>().WithMany().HasForeignKey(x => x.CaptainPlayerId);

        // RosterPlayer is "an Entity within GameweekRoster" (Architecture §6.5).
        builder.HasMany(x => x.Players)
            .WithOne()
            .HasForeignKey(x => x.GameweekRosterId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RosterPlayerConfiguration : IEntityTypeConfiguration<RosterPlayer>
{
    public void Configure(EntityTypeBuilder<RosterPlayer> builder)
    {
        builder.ToTable("roster_players");
        builder.HasKey(x => new { x.GameweekRosterId, x.PlayerId });
        builder.HasIndex(x => x.GameweekRosterId).HasFilter("is_captain").IsUnique();

        builder.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}
