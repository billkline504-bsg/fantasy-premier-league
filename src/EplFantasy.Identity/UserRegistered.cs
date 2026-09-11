using EplFantasy.SharedKernel;

namespace EplFantasy.Identity;

/// <summary>Architecture v1.15 §7's Identity-context domain event catalog. Constructed by IUserAccountService.RegisterAsync on a successful registration; nothing dispatches/consumes it yet (no generic domain-event outbox exists in this codebase — see IT-F12's NotificationRequest outbox for the closest analogous, but unrelated, mechanism), so a future feature task (e.g. a welcome notification) is what would first subscribe to it.</summary>
public sealed record UserRegistered(Guid UserId, string Username, DateTimeOffset OccurredAt) : IDomainEvent;
