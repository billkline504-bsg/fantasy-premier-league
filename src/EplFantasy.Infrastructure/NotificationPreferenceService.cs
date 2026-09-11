using EplFantasy.Notifications;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

public sealed class NotificationPreferenceService(EplFantasyDbContext dbContext) : INotificationPreferenceService
{
    public async Task<IReadOnlyList<NotificationPreference>> GetPreferencesAsync(Guid leagueMembershipId, CancellationToken cancellationToken = default) =>
        await dbContext.NotificationPreferences.AsNoTracking()
            .Where(p => p.LeagueMembershipId == leagueMembershipId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<NotificationPreference>> UpdatePreferencesAsync(
        Guid leagueMembershipId, IReadOnlyList<NotificationPreferenceChange> changes, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.NotificationPreferences
            .Where(p => p.LeagueMembershipId == leagueMembershipId)
            .ToDictionaryAsync(p => (p.EventType, p.Channel), cancellationToken);

        foreach (var change in changes)
        {
            // Every row already exists — seeded the instant this LeagueMembership was created
            // (NotificationPreferenceSeeder) — so this is always an update, never an insert.
            existing[(change.EventType, change.Channel)].Enabled = change.Enabled;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return existing.Values.ToList();
    }
}
