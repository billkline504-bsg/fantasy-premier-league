using EplFantasy.SharedKernel;

namespace EplFantasy.Drafts;

/// <summary>Architecture v1.15 §7's Draft-context domain event catalog (BR-282, BR-323/BR-325). Constructed (as a log entry, not yet a dispatched object — see PlayerDrafted's own remarks for why) by the ADR-012 sweep's DraftPickTimeoutSweepHandler on every skip — a first-time regular-round timeout (IsRequeue = false) or a makeup pick timing out again (IsRequeue = true, BR-325).</summary>
public sealed record DraftPickSkipped(Guid DraftId, Guid FantasyTeamId, bool IsRequeue, DateTimeOffset OccurredAt) : IDomainEvent;
