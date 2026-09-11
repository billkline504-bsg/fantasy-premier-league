namespace EplFantasy.SharedKernel;

/// <summary>
/// The only sanctioned source of "now" anywhere in the domain or application layers. Every
/// deadline/timer-dependent rule — BR-029 invitation expiration, BR-057/BR-058 draft timer,
/// BR-093/BR-094 roster lock, BR-282 pick timeout, the ADR-012 background sweep — must resolve
/// "now" through an injected <see cref="IClock"/> rather than calling
/// <see cref="DateTimeOffset.UtcNow"/> or <see cref="DateTime.UtcNow"/> directly, so those rules
/// can be tested deterministically instead of racing the real wall clock (Testing Strategy v1.0
/// §4). <c>EplFantasy.Infrastructure.ClockUsageTests</c> (EplFantasy.UnitTests) enforces this by
/// scanning every module's compiled IL for a direct call to the wall clock — this is not left as
/// an unenforced convention.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
