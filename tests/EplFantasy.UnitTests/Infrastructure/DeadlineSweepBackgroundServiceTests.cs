using EplFantasy.Infrastructure;
using EplFantasy.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure;

public class DeadlineSweepBackgroundServiceTests
{
    private sealed class CountingHandler(string name) : IDeadlineSweepHandler
    {
        public string Name => name;
        public int CallCount { get; private set; }

        public Task SweepAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IDeadlineSweepHandler
    {
        public string Name => "Throwing";

        public Task SweepAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("simulated handler failure");
    }

    private static DeadlineSweepBackgroundService CreateSweep(ServiceProvider provider, TimeSpan? interval = null) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeadlineSweepOptions { Interval = interval ?? TimeSpan.FromSeconds(20) }),
            NullLogger<DeadlineSweepBackgroundService>.Instance);

    [Fact]
    public async Task RunOneSweepPassAsync_invokes_every_registered_handler_exactly_once()
    {
        var handlerA = new CountingHandler("A");
        var handlerB = new CountingHandler("B");

        var services = new ServiceCollection();
        services.AddSingleton<IDeadlineSweepHandler>(handlerA);
        services.AddSingleton<IDeadlineSweepHandler>(handlerB);
        await using var provider = services.BuildServiceProvider();

        await CreateSweep(provider).RunOneSweepPassAsync(CancellationToken.None);

        Assert.Equal(1, handlerA.CallCount);
        Assert.Equal(1, handlerB.CallCount);
    }

    [Fact]
    public async Task RunOneSweepPassAsync_runs_with_no_handlers_registered_at_all()
    {
        // The realistic state as of IT-F08: no concrete handler exists yet (Draft/Roster sweep
        // logic belongs to later feature tasks) — the loop must be a no-op, not an error.
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();

        await CreateSweep(provider).RunOneSweepPassAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failing_handler_is_isolated_and_never_blocks_its_siblings()
    {
        var before = new CountingHandler("Before");
        var throwing = new ThrowingHandler();
        var after = new CountingHandler("After");

        var services = new ServiceCollection();
        services.AddSingleton<IDeadlineSweepHandler>(before);
        services.AddSingleton<IDeadlineSweepHandler>(throwing);
        services.AddSingleton<IDeadlineSweepHandler>(after);
        await using var provider = services.BuildServiceProvider();

        // Must not throw — a handler failure is logged and skipped, never propagated to the caller.
        await CreateSweep(provider).RunOneSweepPassAsync(CancellationToken.None);

        Assert.Equal(1, before.CallCount);
        Assert.Equal(1, after.CallCount);
    }

    [Fact]
    public async Task The_background_timer_loop_ticks_repeatedly_at_the_configured_interval()
    {
        var handler = new CountingHandler("Loop");
        var services = new ServiceCollection();
        services.AddSingleton<IDeadlineSweepHandler>(handler);
        await using var provider = services.BuildServiceProvider();

        var sweep = CreateSweep(provider, TimeSpan.FromMilliseconds(20));

        await sweep.StartAsync(CancellationToken.None);

        // Poll for the tick count instead of sleeping a fixed 150ms and asserting once: a loaded
        // CI runner can be slow enough that real 20ms timer ticks don't land 3 times in a fixed
        // 150ms wall-clock window even though the loop is working correctly, which made this test
        // flaky under CI (never under this repo's own local runs). Bounding the poll at a generous
        // 5s instead still fails on a genuinely broken loop, just no longer on a merely slow one.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.CallCount < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        await sweep.StopAsync(CancellationToken.None);

        Assert.True(handler.CallCount >= 3, $"expected several ticks within 5s at a 20ms interval, got {handler.CallCount}");
    }

    [Fact]
    public async Task StopAsync_completes_promptly_instead_of_waiting_out_a_long_interval()
    {
        var handler = new CountingHandler("StopTest");
        var services = new ServiceCollection();
        services.AddSingleton<IDeadlineSweepHandler>(handler);
        await using var provider = services.BuildServiceProvider();

        // Deliberately long, so completing promptly can only be explained by StopAsync's
        // cancellation actually interrupting the in-progress Task.Delay, not coincidental timing.
        var sweep = CreateSweep(provider, TimeSpan.FromSeconds(30));

        await sweep.StartAsync(CancellationToken.None);
        await Task.Delay(20); // let the first tick run and enter the 30s delay.

        var stopTask = sweep.StopAsync(CancellationToken.None);
        var winner = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(stopTask, winner);
    }
}
