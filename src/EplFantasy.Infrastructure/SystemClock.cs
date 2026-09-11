using EplFantasy.SharedKernel;

namespace EplFantasy.Infrastructure;

/// <summary>
/// The one place in the entire solution allowed to read the real wall clock — every other
/// assembly resolves "now" through the injected <see cref="IClock"/> instead (see that
/// interface's remarks, and <c>ClockUsageTests</c> in EplFantasy.UnitTests, which enforces this by
/// scanning IL for a direct call to <see cref="DateTimeOffset.UtcNow"/>/<see cref="DateTime.UtcNow"/>
/// anywhere outside this one class).
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
