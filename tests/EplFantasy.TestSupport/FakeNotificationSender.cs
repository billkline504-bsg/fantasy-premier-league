using EplFantasy.Notifications;

namespace EplFantasy.TestSupport;

/// <summary>A controllable INotificationSender for testing NotificationOutboxBackgroundService without a real email/SMS provider (Architecture §15 — that choice isn't made yet).</summary>
public sealed class FakeNotificationSender(NotificationChannel channel) : INotificationSender
{
    public NotificationChannel Channel { get; } = channel;

    public List<NotificationRequest> SentRequests { get; } = [];

    public bool ShouldFail { get; set; }

    public Task SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        if (ShouldFail)
        {
            throw new InvalidOperationException("Simulated send failure.");
        }

        SentRequests.Add(request);
        return Task.CompletedTask;
    }
}
