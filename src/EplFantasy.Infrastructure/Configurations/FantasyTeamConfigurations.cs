using EplFantasy.FantasyTeams;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V005__fantasy_team.sql. SquadPlayer has no
// navigation collection on FantasyTeam (unlike DraftSelection/RosterPlayer on their parent
// aggregates) — it is queried independently across several different shapes (Squad View, Draft
// Player Pool ownership checks, replacement eligibility), so a plain DbSet<SquadPlayer> better
// matches its actual access patterns than an always-loaded collection would. See
// IdentityConfigurations.cs's header comment for why every FK is still declared without a
// navigation property.

public class FantasyTeamConfiguration : IEntityTypeConfiguration<FantasyTeam>
{
    public void Configure(EntityTypeBuilder<FantasyTeam> builder)
    {
        builder.ToTable("fantasy_teams");
        builder.HasKey(x => x.FantasyTeamId);
        builder.HasIndex(x => new { x.LeagueMembershipId, x.SeasonId }).IsUnique();

        builder.HasOne<LeagueMembership>().WithMany().HasForeignKey(x => x.LeagueMembershipId);
        builder.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId);
    }
}

public class SquadPlayerConfiguration : IEntityTypeConfiguration<SquadPlayer>
{
    public void Configure(EntityTypeBuilder<SquadPlayer> builder)
    {
        builder.ToTable("squad_players");
        builder.HasKey(x => x.SquadPlayerId);

        // BR-035/BR-191, Invariant 1 — the constraint AP-009/AP-010's atomic draft-pick guarantee
        // relies on (see run_concurrency_test.sh, Testing Strategy v1.0 §3).
        builder.HasIndex(x => new { x.PlayerId, x.SeasonId }).HasFilter("is_currently_owned").IsUnique();

        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.FantasyTeamId);
        builder.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
        builder.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId);
    }
}
