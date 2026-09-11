using EplFantasy.Infrastructure;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure;

public class NotificationOutboxOptionsTests
{
    private static readonly NotificationOutboxOptions Options = new()
    {
        BaseBackoff = TimeSpan.FromMinutes(1),
        MaxBackoff = TimeSpan.FromHours(1),
    };

    [Fact]
    public void Zero_attempts_has_no_backoff()
    {
        Assert.Equal(TimeSpan.Zero, Options.BackoffFor(0));
    }

    [Fact]
    public void The_first_attempt_backs_off_by_exactly_the_base_amount()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), Options.BackoffFor(1));
    }

    [Fact]
    public void Backoff_doubles_with_each_further_attempt()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), Options.BackoffFor(2));
        Assert.Equal(TimeSpan.FromMinutes(4), Options.BackoffFor(3));
        Assert.Equal(TimeSpan.FromMinutes(8), Options.BackoffFor(4));
    }

    [Fact]
    public void Backoff_never_exceeds_the_configured_maximum()
    {
        var backoff = Options.BackoffFor(100); // would be astronomically large uncapped.
        Assert.Equal(Options.MaxBackoff, backoff);
    }

    [Fact]
    public void Backoff_is_monotonically_non_decreasing_as_attempts_increase()
    {
        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var current = Options.BackoffFor(attempt);
            Assert.True(current >= previous, $"backoff decreased between attempt {attempt - 1} and {attempt}");
            previous = current;
        }
    }
}
