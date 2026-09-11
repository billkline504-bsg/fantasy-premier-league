using EplFantasy.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EplFantasy.Infrastructure;

/// <summary>
/// ADR-012's "one IDeadlineSweepJob-style background service, parameterized per aggregate type."
/// Every registered <see cref="IDeadlineSweepHandler"/> runs once per tick, each in isolation —
/// one handler throwing is logged and skipped for that tick, never allowed to block sibling
/// handlers or bring down the sweep loop (the whole point of a dependable periodic mechanism).
/// A fresh DI scope is created per tick so handlers can depend on scoped services (notably
/// <see cref="EplFantasyDbContext"/>) exactly the way a request-scoped application service would.
/// </summary>
public sealed class DeadlineSweepBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<DeadlineSweepOptions> options,
    ILogger<DeadlineSweepBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.Interval;

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOneSweepPassAsync(stoppingToken);

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
    /// Runs exactly one sweep pass — every registered handler, once — and returns once they've
    /// all finished. Exposed publicly (not just reachable via the timer loop) so it can be
    /// invoked deterministically, e.g. from a test or an operator-triggered diagnostic endpoint,
    /// without waiting on <see cref="DeadlineSweepOptions.Interval"/>.
    /// </summary>
    public async Task RunOneSweepPassAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IDeadlineSweepHandler>();

        foreach (var handler in handlers)
        {
            try
            {
                await handler.SweepAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Deadline sweep handler {HandlerName} failed; skipped for this tick", handler.Name);
            }
        }
    }
}
