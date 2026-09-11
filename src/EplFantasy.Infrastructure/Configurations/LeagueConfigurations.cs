using EplFantasy.Identity;
using EplFantasy.Leagues;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V004__league_and_season.sql.
//
// No EF navigation PROPERTIES are declared between separate Aggregate Roots (League,
// LeagueMembership, Season, Invitation, LeagueMessage are each their own aggregate per
// Architecture §6.2) — cross-aggregate references are plain scalar Guid columns on the C# side,
// resolved by explicit query/join when needed, never by object-graph navigation. The underlying
// FK *relationship* is still declared via HasOne(...).HasForeignKey(...) with no navigation
// argument on either end, because EF Core's SaveChanges() needs that relationship metadata to
// compute a correct insert order across a multi-aggregate save — without it, EF has no idea
// LeagueMembership must be inserted before FantasyTeam, and will pick an arbitrary order that
// then fails against the database's real FK constraint (this is exactly what
// EplFantasyDbContextTests caught during IT-F03's own validation).
//
// The ONE deliberate exception: League.CreatedByMembershipId -> LeagueMembership is NOT declared
// here. That relationship is genuinely circular at the NOT NULL/NOT NULL level (LeagueMembership
// also requires LeagueId -> League), which EF Core's own save-order algorithm cannot resolve on
// its own (it would need at least one side nullable to break the cycle by inserting-then-updating,
// which BR-024's schema doesn't have). PostgreSQL's DEFERRABLE INITIALLY DEFERRED constraint on
// fk_leagues_created_by_membership is what actually resolves this, checked only at COMMIT — a
// mechanism the database has that EF Core's client-side ordering algorithm does not, and doesn't
// need to know about: leaving this one relationship undeclared means EF sends both INSERTs without
// trying to reorder them, and Postgres does the rest (proven in Database Migration Strategy v1.0
// §2 and re-proven end-to-end in EplFantasyDbContextTests). Application code creating a League
// still adds both entities and calls SaveChanges() once, exactly as before.
//
// Classes here are suffixed "EntityConfiguration" rather than the shorter "...Configuration" that
// most other files use, specifically because two of this context's own domain types are
// themselves named LeagueConfiguration/SeasonConfiguration — the shorter suffix would collide
// with those type names in this file.

public class LeagueEntityConfiguration : IEntityTypeConfiguration<League>
{
    public void Configure(EntityTypeBuilder<League> builder)
    {
        builder.ToTable("leagues");
        builder.HasKey(x => x.LeagueId);

        // Deliberately NOT declared: HasOne<LeagueMembership>().HasForeignKey(x => x.CreatedByMembershipId).
        // See this file's header comment.
    }
}

public class LeagueMembershipEntityConfiguration : IEntityTypeConfiguration<LeagueMembership>
{
    public void Configure(EntityTypeBuilder<LeagueMembership> builder)
    {
        builder.ToTable("league_memberships");
        builder.HasKey(x => x.LeagueMembershipId);

        builder.HasIndex(x => new { x.LeagueId, x.UserId }).HasFilter("status <> 'left'").IsUnique();
        builder.HasIndex(x => x.LeagueId).HasFilter("is_administrator").IsUnique();

        builder.HasOne<League>().WithMany().HasForeignKey(x => x.LeagueId);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        builder.HasOne<ProfileIcon>().WithMany().HasForeignKey(x => x.LeagueIconId);
    }
}

public class LeagueConfigurationEntityConfiguration : IEntityTypeConfiguration<LeagueConfiguration>
{
    public void Configure(EntityTypeBuilder<LeagueConfiguration> builder)
    {
        builder.ToTable("league_configurations");
        builder.HasKey(x => x.LeagueId);

        builder.HasOne<League>().WithOne().HasForeignKey<LeagueConfiguration>(x => x.LeagueId);
        builder.HasOne<LeagueMembership>().WithMany().HasForeignKey(x => x.UpdatedByMembershipId);
    }
}

public class SeasonEntityConfiguration : IEntityTypeConfiguration<Season>
{
    public void Configure(EntityTypeBuilder<Season> builder)
    {
        builder.ToTable("seasons");
        builder.HasKey(x => x.SeasonId);

        builder.HasOne<League>().WithMany().HasForeignKey(x => x.LeagueId);
        builder.HasOne<PlayerData.EplSeason>().WithMany().HasForeignKey(x => x.EplSeasonIdentifier);
    }
}

public class SeasonConfigurationEntityConfiguration : IEntityTypeConfiguration<SeasonConfiguration>
{
    public void Configure(EntityTypeBuilder<SeasonConfiguration> builder)
    {
        builder.ToTable("season_configurations");
        builder.HasKey(x => x.SeasonId);

        builder.HasOne<Season>().WithOne().HasForeignKey<SeasonConfiguration>(x => x.SeasonId);
    }
}

public class InvitationEntityConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations");
        builder.HasKey(x => x.InvitationId);
        builder.HasIndex(x => x.Token).IsUnique();

        builder.HasOne<League>().WithMany().HasForeignKey(x => x.LeagueId);
        builder.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId);
    }
}

public class LeagueMessageEntityConfiguration : IEntityTypeConfiguration<LeagueMessage>
{
    public void Configure(EntityTypeBuilder<LeagueMessage> builder)
    {
        builder.ToTable("league_messages");
        builder.HasKey(x => x.LeagueMessageId);

        builder.HasOne<League>().WithMany().HasForeignKey(x => x.LeagueId);
        builder.HasOne<LeagueMembership>().WithMany().HasForeignKey(x => x.AuthorMembershipId);
    }
}
