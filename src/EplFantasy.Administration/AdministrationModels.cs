namespace EplFantasy.Administration;

// Persistence shapes for the Corrections & Administration context (Architecture v1.15 §6.8;
// physical schema: 06-database-migrations/migrations/V010__administration.sql).

public enum AdminActionType
{
    RosterCorrection,
    ScoreOverride,
    ScoreOverrideUndo,
    ReplacementEligibilityGranted,
    SeasonEndingInjuryDeclared,
    DraftTimerExtended,
    ConfigurationChanged,
    Other,
}

public enum SecurityEventType
{
    RateLimitBlocked,
}

/// <summary>
/// BR-149/AP-005: append-only audit log. The database role revokes UPDATE/DELETE on this table
/// entirely (ADR-010, Database Migration Strategy v1.0 §3/V013) — enforced below the application
/// layer, not just by convention here.
/// </summary>
public class AdministrativeAction
{
    public Guid ActionId { get; set; }
    public Guid LeagueId { get; set; }

    /// <summary>Null means system-generated (e.g. BR-308's automatic EPL-exit eligibility grant) — render as "System".</summary>
    public Guid? ActingMembershipId { get; set; }

    public AdminActionType ActionType { get; set; }
    public string TargetEntityType { get; set; } = null!;
    public Guid TargetEntityId { get; set; }
    public string BeforeStateJson { get; set; } = null!;
    public string AfterStateJson { get; set; } = null!;
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// BR-170/BR-327: platform-level (not League-scoped) log written directly by the rate-limiting
/// middleware on every enforced block.
/// </summary>
public class SecurityEvent
{
    public Guid SecurityEventId { get; set; }
    public SecurityEventType EventType { get; set; }
    public string Endpoint { get; set; } = null!;
    public string Scope { get; set; } = null!;
    public string Detail { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
}
