using EplFantasy.SharedKernel;

namespace EplFantasy.Scoring;

/// <summary>Architecture v1.15 §7's Scoring-context domain event catalog. Constructed (as a log entry, not yet a dispatched object — see Identity's UsernameChanged for why) by IPlayerPerformanceSyncService.SyncGameweekAsync when a re-sync detects a genuine delta against an already-stored PlayerPerformance (BR-139/BR-234) — never for a first-time ingestion or an unchanged re-sync.</summary>
public sealed record ScoreRecalculated(Guid PlayerId, Guid GameweekId, int OldFantasyPoints, int NewFantasyPoints, DateTimeOffset OccurredAt) : IDomainEvent;
