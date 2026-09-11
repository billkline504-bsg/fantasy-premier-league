namespace EplFantasy.Notifications;

/// <summary>
/// IT-53 (F-012.1, BR-338): every LeagueMembership gets one row per (EventType, Channel)
/// combination the instant it's created — all disabled by default (BR-291-style "safe default,"
/// never opted-in silently) — so <c>NotificationOutboxBackgroundService</c>'s own "missing
/// preference row means suppress" fallback (its own remarks, written before this task existed)
/// should never actually be exercised in practice going forward.
/// </summary>
public static class NotificationPreferenceSeeder
{
    public static IEnumerable<NotificationPreference> SeedFor(Guid leagueMembershipId) =>
        from eventType in Enum.GetValues<NotificationEventType>()
        from channel in Enum.GetValues<NotificationChannel>()
        select new NotificationPreference
        {
            LeagueMembershipId = leagueMembershipId,
            EventType = eventType,
            Channel = channel,
            Enabled = false,
        };
}
