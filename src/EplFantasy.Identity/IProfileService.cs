using EplFantasy.SharedKernel;

namespace EplFantasy.Identity;

/// <summary>
/// F-002.1's default-icon-selection orchestration (IT-09). Declared here, in the bounded context
/// that owns the UserProfile aggregate; implemented in EplFantasy.Infrastructure (it needs the
/// real DbContext), matching every other application-service seam in this codebase.
/// </summary>
public interface IProfileService
{
    /// <summary>
    /// BR-006/BR-011: changes the caller's global default icon. Fails with
    /// <c>"profile_icon_not_active"</c> if profileIconId names no ProfileIcon at all — a real but
    /// inactive one instead throws <see cref="ProfileIconNotActiveException"/> (same error code,
    /// different layer; see that type's own remarks).
    /// </summary>
    Task<Result<UserProfile>> UpdateDefaultIconAsync(Guid userId, Guid profileIconId, CancellationToken cancellationToken = default);
}
