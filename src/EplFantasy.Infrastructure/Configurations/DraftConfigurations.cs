using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V006__draft.sql. DraftOrder/PendingMakeupPicks
// (List<Guid>) map directly to Postgres uuid[] columns via Npgsql's built-in array support — no
// extra configuration needed beyond the property existing. See IdentityConfigurations.cs's header
// comment for why every FK is declared without a navigation property (except Draft.Selections,
// which — unlike everything else here — genuinely is a within-aggregate child collection).

public class DraftConfiguration : IEntityTypeConfiguration<Draft>
{
    public void Configure(EntityTypeBuilder<Draft> builder)
    {
        builder.ToTable("drafts");
        builder.HasKey(x => x.DraftId);

        builder.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId);

        // DraftSelection is "an Entity within Draft aggregate" (Architecture §6.4).
        builder.HasMany(x => x.Selections)
            .WithOne()
            .HasForeignKey(x => x.DraftId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class DraftSelectionConfiguration : IEntityTypeConfiguration<DraftSelection>
{
    public void Configure(EntityTypeBuilder<DraftSelection> builder)
    {
        builder.ToTable("draft_selections");
        builder.HasKey(x => x.DraftSelectionId);
        builder.HasIndex(x => new { x.DraftId, x.Round, x.PickNumber }).IsUnique();

        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.FantasyTeamId);
        builder.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}

public class ReplacementOpportunityConfiguration : IEntityTypeConfiguration<ReplacementOpportunity>
{
    public void Configure(EntityTypeBuilder<ReplacementOpportunity> builder)
    {
        builder.ToTable("replacement_opportunities");
        builder.HasKey(x => x.ReplacementOpportunityId);

        builder.HasOne<FantasyTeam>().WithMany().HasForeignKey(x => x.FantasyTeamId);
        builder.HasOne<Player>().WithMany().HasForeignKey(x => x.SourcePlayerId);
    }
}
