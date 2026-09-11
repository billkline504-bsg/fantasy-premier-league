namespace EplFantasy.Notifications;

/// <summary>A single (EventType, Channel, Enabled) change requested by the caller — the update endpoint's own request shape, never a full replacement of the row set (BR-338: independent per event/channel).</summary>
public sealed record NotificationPreferenceChange(NotificationEventType EventType, NotificationChannel Channel, bool Enabled);

/// <summary>IT-53 (F-012.1, BR-150/BR-151/BR-155/BR-338): self-service CRUD over one LeagueMembership's own notification preferences.</summary>
public interface INotificationPreferenceService
{
    /// <summary>Returns all six (3 event types x 2 channels) rows for <paramref name="leagueMembershipId"/> — always the full set, seeded at membership creation (NotificationPreferenceSeeder).</summary>
    Task<IReadOnlyList<NotificationPreference>> GetPreferencesAsync(Guid leagueMembershipId, CancellationToken cancellationToken = default);

    /// <summary>Applies each requested change to its own already-seeded row and returns the full, refreshed set — takes effect immediately for future notifications only (BR-338 AC4); any already-queued NotificationRequest is unaffected.</summary>
    Task<IReadOnlyList<NotificationPreference>> UpdatePreferencesAsync(Guid leagueMembershipId, IReadOnlyList<NotificationPreferenceChange> changes, CancellationToken cancellationToken = default);
}
