using EplFantasy.SharedKernel;

namespace EplFantasy.Identity;

/// <summary>Architecture v1.15 §7's Identity-context domain event catalog. Constructed (as a log entry, not yet a dispatched object — see UserRegistered's own remarks on why) by IUsernameService.ChangeUsernameAsync on a successful change, in the same transaction that closes the prior UsernameHistory row and opens the new one (BR-326, Feature Behavior Spec F-001.3 AC-5).</summary>
public sealed record UsernameChanged(Guid UserId, string OldUsername, string NewUsername, DateTimeOffset OccurredAt) : IDomainEvent;
