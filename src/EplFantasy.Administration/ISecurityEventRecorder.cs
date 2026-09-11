namespace EplFantasy.Administration;

/// <summary>
/// The mechanism the rate-limiting middleware (IT-F14, Architecture §11, BR-169/BR-170/BR-327)
/// writes a <c>security_events</c> row through on every enforced block. Unlike
/// <see cref="IAdministrativeActionRecorder"/> — which only stages a row for the caller's own
/// <c>SaveChangesAsync()</c> to commit atomically alongside a domain change — a rate-limit
/// rejection never reaches an application service or its own unit-of-work at all (the request is
/// short-circuited before routing gets there), so there is nothing for this call to piggyback on.
/// <see cref="RecordAsync"/> therefore commits itself.
/// </summary>
public interface ISecurityEventRecorder
{
    /// <param name="eventType">Which kind of security event this is.</param>
    /// <param name="endpoint">The request path that was blocked.</param>
    /// <param name="scope">What the limit was keyed on (e.g. "IP 203.0.113.44").</param>
    /// <param name="detail">A human-readable description of what happened.</param>
    Task RecordAsync(
        SecurityEventType eventType,
        string endpoint,
        string scope,
        string detail,
        CancellationToken cancellationToken = default);
}
