namespace EplFantasy.Notifications;

/// <summary>
/// Architecture §12.2/§15: "the specific email/SMS provider is an infrastructure choice deferred
/// alongside hosting (ADR-005), not a domain concern" — this interface is the plug point.
/// EplFantasy.Infrastructure.NotificationOutboxBackgroundService drains
/// <c>notification_requests</c> and resolves the sender matching each request's Channel; no
/// concrete implementation exists yet as of this foundational task (IT-F12) since that provider
/// choice hasn't been made — F-012.4 (IT-54) wires a real one in once it has.
/// </summary>
public interface INotificationSender
{
    NotificationChannel Channel { get; }

    /// <summary>Throws on failure — the outbox dispatcher catches it, applies BR-225's backoff, and retries on a later sweep.</summary>
    Task SendAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}
