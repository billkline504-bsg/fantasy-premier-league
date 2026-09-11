namespace EplFantasy.Infrastructure;

/// <summary>Bound from configuration (the "NotificationOutbox" section).</summary>
public sealed class NotificationOutboxOptions
{
    public const string SectionName = "NotificationOutbox";

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromHours(1);

    /// <summary>After this many failed attempts, a request stops being retried (Status = Failed) rather than retrying forever.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// BR-225 "retries failures with backoff": exponential (BaseBackoff × 2^(attempts−1)),
    /// capped at MaxBackoff. Pure and side-effect-free so it's directly unit-testable without
    /// spinning up the background service or a database.
    /// </summary>
    public TimeSpan BackoffFor(int attempts)
    {
        if (attempts <= 0)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, attempts - 1);
        var scaledTicks = BaseBackoff.Ticks * multiplier;

        // Guard against overflow for a pathologically large attempts count — MaxBackoff already
        // caps the practical result, this just keeps the intermediate multiplication in range.
        if (scaledTicks >= MaxBackoff.Ticks || double.IsInfinity(scaledTicks))
        {
            return MaxBackoff;
        }

        var delay = TimeSpan.FromTicks((long)scaledTicks);
        return delay > MaxBackoff ? MaxBackoff : delay;
    }
}
