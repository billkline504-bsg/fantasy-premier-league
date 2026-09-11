using EplFantasy.SharedKernel;

namespace EplFantasy.Identity;

/// <summary>
/// One of the two points where the rest of the system is allowed to depend on "who is logged in"
/// (ADR-006; the other is <see cref="ICurrentUserAccessor"/>, SharedKernel). Declared here, in the
/// bounded context that owns authentication, but implemented in EplFantasy.Infrastructure (it
/// needs the real DbContext to persist/rotate refresh tokens) — this is exactly the isolation
/// ADR-006 asks for: "the authentication module is isolated behind an interface so an external
/// Identity Provider (OIDC/SAML) can be introduced later (BR-157) without touching the rest of
/// the domain." No module outside this interface's implementation reads a JWT or a password hash
/// directly.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>BR-158: adaptive hash only, never reversible/plaintext.</summary>
    string HashPassword(string plainTextPassword);

    bool VerifyPassword(string plainTextPassword, string passwordHash);

    /// <summary>
    /// Issues a new short-lived access token and a new rotating refresh token for the given User
    /// (BR-159), persisting the refresh token's hash. Used both at login and whenever a feature
    /// task's own flow (registration, etc.) needs to establish a session.
    /// </summary>
    Task<AuthTokenResult> IssueTokensAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// BR-159 "rotating refresh tokens": the presented refresh token is revoked and replaced by a
    /// new one in the same call, never reused. Fails if the token is unknown, already revoked, or
    /// expired.
    /// </summary>
    Task<Result<AuthTokenResult>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes a single refresh token (logout). Idempotent — revoking an already-revoked or unknown token is not an error.</summary>
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
}

/// <param name="UserId">Whose session this is — the same value encoded as the access token's <c>sub</c> claim, exposed directly so a caller (e.g. AuthController.Refresh) doesn't need to decode the JWT just to know whose data to return alongside it.</param>
/// <param name="AccessToken">A short-lived bearer JWT carrying only <c>sub</c> (UserId) — never roles/permissions (ADR-007, Architecture §9.1).</param>
/// <param name="AccessTokenExpiresAt">When <paramref name="AccessToken"/> stops being valid.</param>
/// <param name="RefreshToken">The raw (unhashed) refresh token value — the only time it is ever available in plaintext; only its hash is persisted.</param>
public sealed record AuthTokenResult(Guid UserId, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);
