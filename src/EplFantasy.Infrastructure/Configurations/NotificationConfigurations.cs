using EplFantasy.Identity;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

// Mirrors 06-database-migrations/migrations/V011__notifications.sql. See
// IdentityConfigurations.cs's header comment for why every FK is declared without a navigation
// property.

public class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("notification_preferences");
        builder.HasKey(x => new { x.LeagueMembershipId, x.EventType, x.Channel });

        builder.HasOne<LeagueMembership>().WithMany().HasForeignKey(x => x.LeagueMembershipId);
    }
}

public class NotificationRequestConfiguration : IEntityTypeConfiguration<NotificationRequest>
{
    public void Configure(EntityTypeBuilder<NotificationRequest> builder)
    {
        builder.ToTable("notification_requests");
        builder.HasKey(x => x.RequestId);

        builder.Property(x => x.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");

        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        builder.HasOne<LeagueMembership>().WithMany().HasForeignKey(x => x.LeagueMembershipId);
    }
}
