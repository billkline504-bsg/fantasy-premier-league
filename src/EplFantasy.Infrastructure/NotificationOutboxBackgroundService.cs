using EplFantasy.Notifications;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EplFantasy.Infrastructure;

/// <summary>
/// Architecture §12.2's outbox-draining background worker: on each tick, finds every
/// <c>Pending</c> NotificationRequest whose backoff window (if any) has elapsed, checks the
/// matching NotificationPreference (BR-226 — a disabled or missing preference row suppresses the
/// request rather than sending it), and hands eligible ones to whichever
/// <see cref="INotificationSender"/> is registered for that request's channel. A failure is
/// caught per-request (never allowed to block sibling requests, matching
/// DeadlineSweepBackgroundService's isolation pattern) and retried with backoff (BR-225) up to
/// <see cref="NotificationOutboxOptions.MaxAttempts"/>, after which the request is marked Failed
/// rather than retried forever.
/// </summary>
public sealed class NotificationOutboxBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationOutboxOptions> options,
    ILogger<NotificationOutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.Interval;

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOneDispatchPassAsync(stoppingToken);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on host shutdown while waiting between ticks.
            }
        }
    }

    /// <summary>
    /// Runs exactly one dispatch pass. Exposed publicly for deterministic testing, the same way
    /// DeadlineSweepBackgroundService.RunOneSweepPassAsync is.
    /// </summary>
    /// <remarks>
    /// Unlike the per-request try/catch below (which isolates one bad request from its siblings),
    /// the whole pass is also wrapped in an outer try/catch. Without it, a transient failure to
    /// even reach the database — the initial query or the final SaveChangesAsync — would throw out
    /// of ExecuteAsync's loop and, under the default BackgroundServiceExceptionBehavior.StopHost,
    /// take down the entire API host over what should be a one-tick blip in a background feature.
    /// </remarks>
    public async Task RunOneDispatchPassAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var sendersByChannel = scope.ServiceProvider.GetServices<INotificationSender>().ToDictionary(s => s.Channel);
        var opts = options.Value;
        var now = clock.UtcNow;

        List<NotificationRequest> pendingRequests;
        try
        {
            pendingRequests = await dbContext.NotificationRequests
                .Where(r => r.Status == NotificationStatus.Pending)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notification outbox dispatch pass could not load pending requests; skipped for this tick");
            return;
        }

        foreach (var request in pendingRequests)
        {
            if (request.LastAttemptAt is { } lastAttempt && lastAttempt + opts.BackoffFor(request.Attempts) > now)
            {
                continue; // still within its backoff window — not eligible this tick.
            }

            try
            {
                var preference = await dbContext.NotificationPreferences.SingleOrDefaultAsync(
                    p => p.LeagueMembershipId == request.LeagueMembershipId
                         && p.EventType == request.EventType
                         && p.Channel == request.Channel,
                    cancellationToken);

                if (preference is not { Enabled: true })
                {
                    // BR-226: a disabled channel/event is never sent. A missing preference row
                    // (shouldn't happen once F-012.1 seeds one at membership creation, but this
                    // dispatcher doesn't assume that feature is built yet) is treated the same —
                    // suppress rather than guess.
                    request.Status = NotificationStatus.Suppressed;
                    continue;
                }

                if (!sendersByChannel.TryGetValue(request.Channel, out var sender))
                {
                    throw new InvalidOperationException(
                        $"No INotificationSender registered for channel {request.Channel} (Architecture §15: provider not yet chosen).");
                }

                await sender.SendAsync(request, cancellationToken);
                request.Status = NotificationStatus.Sent;
                request.LastAttemptAt = now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                request.Attempts++;
                request.LastAttemptAt = now;
                request.Status = request.Attempts >= opts.MaxAttempts
                    ? NotificationStatus.Failed
                    : NotificationStatus.Pending; // stays Pending — the next tick past the backoff window will retry it.

                logger.LogWarning(ex, "NotificationRequest {RequestId} attempt {Attempts} failed", request.RequestId, request.Attempts);
            }
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notification outbox dispatch pass could not save its results; skipped for this tick");
        }
    }
}
