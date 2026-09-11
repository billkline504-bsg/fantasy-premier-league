using EplFantasy.Administration;
using EplFantasy.Leagues;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V010__administration.sql. UPDATE/DELETE on
// administrative_actions are revoked from the runtime role at the database level (V013,
// ADR-010) — nothing to configure in EF for that; it is enforced below the ORM entirely. See
// IdentityConfigurations.cs's header comment for why every FK is declared without a navigation
// property.

public class AdministrativeActionConfiguration : IEntityTypeConfiguration<AdministrativeAction>
{
    public void Configure(EntityTypeBuilder<AdministrativeAction> builder)
    {
        builder.ToTable("administrative_actions");
        builder.HasKey(x => x.ActionId);

        builder.Property(x => x.BeforeStateJson).HasColumnName("before_state").HasColumnType("jsonb");
        builder.Property(x => x.AfterStateJson).HasColumnName("after_state").HasColumnType("jsonb");

        builder.HasOne<League>().WithMany().HasForeignKey(x => x.LeagueId);
        builder.HasOne<LeagueMembership>().WithMany().HasForeignKey(x => x.ActingMembershipId);
    }
}

public class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("security_events");
        builder.HasKey(x => x.SecurityEventId);
    }
}
