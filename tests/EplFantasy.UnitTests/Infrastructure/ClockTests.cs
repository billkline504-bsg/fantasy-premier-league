using EplFantasy.Infrastructure;
using EplFantasy.TestSupport;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure;

public class SystemClockTests
{
    [Fact]
    public void UtcNow_tracks_the_real_wall_clock()
    {
        var clock = new SystemClock();

        var before = DateTimeOffset.UtcNow;
        var reading = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(reading, before, after);
    }
}

public class FakeClockTests
{
    [Fact]
    public void StartingAt_sets_the_initial_reading()
    {
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = FakeClock.StartingAt(start);

        Assert.Equal(start, clock.UtcNow);
    }

    [Fact]
    public void AdvanceTo_sets_an_exact_new_reading()
    {
        var clock = FakeClock.StartingAt(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var target = new DateTimeOffset(2026, 9, 15, 12, 30, 0, TimeSpan.Zero);

        clock.AdvanceTo(target);

        Assert.Equal(target, clock.UtcNow);
    }

    [Fact]
    public void AdvanceBy_adds_the_given_duration()
    {
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = FakeClock.StartingAt(start);

        clock.AdvanceBy(TimeSpan.FromDays(7));

        Assert.Equal(start + TimeSpan.FromDays(7), clock.UtcNow);
    }

    [Fact]
    public void The_clock_never_moves_on_its_own()
    {
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = FakeClock.StartingAt(start);

        Thread.Sleep(50);

        Assert.Equal(start, clock.UtcNow);
    }
}
