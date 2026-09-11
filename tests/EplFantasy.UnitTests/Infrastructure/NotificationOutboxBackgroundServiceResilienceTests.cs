using EplFantasy.Infrastructure;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure;

/// <summary>
/// Proves a fix for a real IT-F12 boot-time bug: RunOneDispatchPassAsync used to run its initial
/// query and final SaveChangesAsync completely unguarded, so an unreachable database threw out of
/// ExecuteAsync's loop and, under the default BackgroundServiceExceptionBehavior.StopHost, took
/// down the entire API host over what should be a one-tick blip in a background feature. No
/// Testcontainers container is started here on purpose — the whole point is that the database is
/// never actually reachable.
/// </summary>
public class NotificationOutboxBackgroundServiceResilienceTests
{
    [Fact]
    public async Task An_unreachable_database_is_logged_and_skipped_instead_of_throwing()
    {
        // Port 1 refuses connections immediately on every platform this runs on, and the short
        // Timeout bounds how long a misconfigured connection could otherwise hang for.
        const string unreachableConnectionString =
            "Host=127.0.0.1;Port=1;Database=eplfantasy_unreachable;Username=test;Password=test;Timeout=2;Command Timeout=2";

        var services = new ServiceCollection();
        services.AddInfrastructure(unreachableConnectionString);
        services.AddSingleton<IClock>(FakeClock.StartingAt(DateTimeOffset.UtcNow));
        services.AddSingleton(Options.Create(new NotificationOutboxOptions()));
        await using var provider = services.BuildServiceProvider();

        var dispatcher = new NotificationOutboxBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<NotificationOutboxOptions>>(),
            NullLogger<NotificationOutboxBackgroundService>.Instance);

        // Must not throw — the very defect this test exists to guard against.
        await dispatcher.RunOneDispatchPassAsync(CancellationToken.None);
    }
}
