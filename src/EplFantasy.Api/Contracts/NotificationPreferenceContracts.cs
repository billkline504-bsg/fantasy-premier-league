using EplFantasy.Notifications;

namespace EplFantasy.Api.Contracts;

// IT-53 (F-012.1): mirrors OpenAPI's NotificationPreference schema exactly — the same shape used
// for both getNotificationPreferences' response and updateNotificationPreferences' request body.

public sealed class NotificationPreferenceDto
{
    public required Guid LeagueMembershipId { get; init; }
    public required string EventType { get; init; }
    public required string Channel { get; init; }
    public required bool Enabled { get; init; }

    public static NotificationPreferenceDto From(NotificationPreference preference) => new()
    {
        LeagueMembershipId = preference.LeagueMembershipId,
        EventType = preference.EventType.ToString(),
        Channel = preference.Channel.ToString(),
        Enabled = preference.Enabled,
    };
}
