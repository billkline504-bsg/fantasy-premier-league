namespace EplFantasy.Notifications;

// Persistence shapes for the Notifications context (Architecture v1.15 §6.9; physical schema:
// 06-database-migrations/migrations/V011__notifications.sql).

public enum NotificationEventType
{
    GameweekReminder,
    WeeklyScore,
    WeeklyStandings,
}

public enum NotificationChannel
{
    Email,
    Sms,
}

public enum NotificationStatus
{
    Pending,
    Sent,
    Failed,
    Suppressed,
}

/// <summary>BR-338: keyed per LeagueMembership, not per User — a User in three Leagues holds up to three independent rows per (event type, channel).</summary>
public class NotificationPreference
{
    public Guid LeagueMembershipId { get; set; }
    public NotificationEventType EventType { get; set; }
    public NotificationChannel Channel { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>BR-224/BR-225: outbox entry drained by a background worker (Architecture §12.2).</summary>
public class NotificationRequest
{
    public Guid RequestId { get; set; }
    public Guid UserId { get; set; }
    public Guid LeagueMembershipId { get; set; }
    public NotificationEventType EventType { get; set; }
    public NotificationChannel Channel { get; set; }
    public string PayloadJson { get; set; } = null!;
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
}
