namespace EplFantasy.SharedKernel;

/// <summary>
/// ADR-012: one shared background sweep mechanism for every "has a deadline passed, and if so,
/// what fallback applies?" rule — draft-pick timeout (BR-282) and Gameweek roster-lock detection
/// (BR-093/BR-094) are the two Architecture names, plugged into
/// <c>EplFantasy.Infrastructure.DeadlineSweepBackgroundService</c> as separate handlers rather
/// than two independently reinvented polling mechanisms. A handler owns its aggregate type
/// entirely — querying for overdue instances, applying whatever transition applies (skip-and-
/// requeue for a draft pick; lock, or lock-with-carry-forward, for a roster), and persisting the
/// result — the background service only provides "run me periodically, isolated from sibling
/// handlers' failures." No handler exists yet as of this foundational task (IT-F08); concrete
/// ones (e.g. a future DraftPickTimeoutSweepHandler) are added by the feature tasks that own that
/// domain logic (IT-27, IT-31) and register themselves via DI — this interface and the background
/// service that drives it don't need to change when that happens.
/// </summary>
public interface IDeadlineSweepHandler
{
    /// <summary>A short, stable name for logging/diagnostics — e.g. "DraftPickTimeout", "GameweekRosterLock".</summary>
    string Name { get; }

    /// <summary>
    /// Finds every instance of this handler's aggregate type whose deadline has passed as of "now"
    /// (via the handler's own injected IClock, never the wall clock directly) and applies the
    /// appropriate transition. Called once per sweep tick; must not throw for a handler-internal
    /// reason the background service can't act on — an unhandled exception here is logged and
    /// isolated by the caller, but still means this handler's work was skipped for that tick.
    /// </summary>
    Task SweepAsync(CancellationToken cancellationToken);
}
