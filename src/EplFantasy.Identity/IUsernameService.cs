using EplFantasy.SharedKernel;

namespace EplFantasy.Identity;

/// <summary>
/// F-001.3's username-change orchestration (IT-14). Declared here, in the bounded context that
/// owns the User/UsernameHistory aggregates; implemented in EplFantasy.Infrastructure (it needs
/// the real DbContext), matching every other application-service seam in this codebase.
/// </summary>
public interface IUsernameService
{
    /// <summary>
    /// BR-003/BR-004/BR-270: changes the caller's username, closing the currently-open
    /// UsernameHistory row and opening a new one in the same transaction (BR-326). A no-op
    /// (returns success without writing anything) if newUsername is identical, including case, to
    /// the User's current one. Fails with <c>"username_taken"</c> if newUsername is already held
    /// by a different active User (BR-266, case-insensitive) — re-validated here even though
    /// RegisterRequest-style shape validation already ran at the DTO layer (BR-166).
    /// </summary>
    Task<Result<User>> ChangeUsernameAsync(Guid userId, string newUsername, CancellationToken cancellationToken = default);
}
