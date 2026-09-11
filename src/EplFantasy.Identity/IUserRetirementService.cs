namespace EplFantasy.Identity;

/// <summary>
/// F-001.4's account-retirement orchestration (IT-15). Declared here, in the bounded context that
/// owns the User aggregate; implemented in EplFantasy.Infrastructure (it needs the real DbContext),
/// matching every other application-service seam in this codebase.
/// </summary>
public interface IUserRetirementService
{
    /// <summary>BR-013: marks the caller's own account Retired — a soft delete only (ADR-010), never a physical row delete. Idempotent if already retired.</summary>
    Task RetireAsync(Guid userId, CancellationToken cancellationToken = default);
}
