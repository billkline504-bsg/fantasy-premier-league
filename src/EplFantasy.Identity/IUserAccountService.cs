using EplFantasy.SharedKernel;

namespace EplFantasy.Identity;

/// <summary>
/// F-001.1's registration/login orchestration: the domain-level checks (BR-004 uniqueness,
/// BR-285 password strength) IT-01 asks for, layered in front of the token/hash mechanics
/// <see cref="IAuthenticationService"/> already provides (ADR-006). Declared here, in the bounded
/// context that owns the User aggregate; implemented in EplFantasy.Infrastructure (it needs the
/// real DbContext), matching every other application-service seam in this codebase.
/// </summary>
public interface IUserAccountService
{
    /// <summary>
    /// Creates the User, its UserProfile (with the catalog's default ProfileIcon, BR-006), and the
    /// first open UsernameHistory row (BR-326), then issues a token pair. Fails with
    /// <c>"username_taken"</c>/<c>"email_taken"</c> (BR-004, active users only — BR-298 lets a
    /// retired user's username/email be reused) or <c>"password_too_weak"</c> (BR-285) before any
    /// row is written.
    /// </summary>
    Task<Result<UserAccountResult>> RegisterAsync(
        string username,
        string email,
        string plainTextPassword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// BR-156: authenticates by username OR email (case-insensitive, active users only) plus
    /// password, then issues a token pair. Fails with the single <c>"invalid_credentials"</c> code
    /// for every failure reason (unknown identifier, wrong password, retired account) — never
    /// revealing which, so a caller can't enumerate valid usernames/emails by observing the error.
    /// </summary>
    Task<Result<UserAccountResult>> AuthenticateAsync(
        string usernameOrEmail,
        string plainTextPassword,
        CancellationToken cancellationToken = default);
}

/// <param name="User">The account.</param>
/// <param name="Profile">Its profile — always present; every User has exactly one.</param>
/// <param name="Tokens">The freshly issued access/refresh token pair.</param>
public sealed record UserAccountResult(User User, UserProfile Profile, AuthTokenResult Tokens);
