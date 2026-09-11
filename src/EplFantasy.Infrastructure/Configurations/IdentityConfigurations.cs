using EplFantasy.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V002__identity.sql exactly. Column names are left to
// EFCore.NamingConventions' snake_case convention (e.g. UserId -> user_id); every table name and
// key is explicit here regardless of whether convention would have guessed correctly, so a reader
// never has to wonder whether a mapping is intentional or accidental.
//
// Every FK from the migrations is declared here via HasOne(...).HasForeignKey(...) with NO
// corresponding navigation property on either entity (LeagueConfigurations.cs's header comment
// explains why: cross-aggregate object-graph navigation is deliberately not modeled). The
// relationship still has to be declared, though — EF Core's SaveChanges() only knows to insert a
// User before a UsernameHistory row that references it if the relationship exists in its model at
// all; a bare scalar Guid property with no declared relationship gives EF no ordering information,
// and it will insert in an arbitrary order and hit the database's real FK constraint. (The one
// deliberate exception — League.CreatedByMembershipId — is documented in LeagueConfigurations.cs.)

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(x => x.UserId);

        // BR-004/BR-298: uniqueness among active users only, enforced by the migration's own
        // partial unique index (ux_users_username_active) — not reproduced as an EF-level
        // constraint, since EF Core cannot express a partial (WHERE status = 'active') index in a
        // database-agnostic way; HasIndex().HasFilter() is Npgsql-specific but kept here anyway so
        // `dotnet ef migrations add` (if ever run for review, Database Migration Strategy v1.0 §1)
        // reflects the real constraint instead of silently omitting it.
        builder.HasIndex(x => x.Username).HasFilter("status = 'active'").IsUnique();
        builder.HasIndex(x => x.Email).HasFilter("status = 'active'").IsUnique();
    }
}

public class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("user_profiles");
        builder.HasKey(x => x.UserId);

        builder.HasOne<User>().WithOne().HasForeignKey<UserProfile>(x => x.UserId);
        builder.HasOne<ProfileIcon>().WithMany().HasForeignKey(x => x.DefaultIconId);
    }
}

public class UsernameHistoryConfiguration : IEntityTypeConfiguration<UsernameHistory>
{
    public void Configure(EntityTypeBuilder<UsernameHistory> builder)
    {
        // Singular table name (BR-326) — the only entity in this context whose plural DbSet
        // property name would not have matched its table name under the naming convention alone.
        builder.ToTable("username_history");
        builder.HasKey(x => x.UsernameHistoryId);

        builder.HasIndex(x => x.UserId).HasFilter("effective_to IS NULL").IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
    }
}

public class ProfileIconConfiguration : IEntityTypeConfiguration<ProfileIcon>
{
    public void Configure(EntityTypeBuilder<ProfileIcon> builder)
    {
        builder.ToTable("profile_icons");
        builder.HasKey(x => x.ProfileIconId);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(x => x.RefreshTokenId);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
    }
}

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("password_reset_tokens");
        builder.HasKey(x => x.PasswordResetTokenId);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
    }
}
