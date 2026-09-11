using EplFantasy.SharedKernel;

namespace EplFantasy.Identity;

/// <summary>
/// F-001.2's password-reset orchestration (IT-13). Declared here, in the bounded context that
/// owns the User/PasswordResetToken aggregates; implemented in EplFantasy.Infrastructure (it needs
/// the real DbContext and IAuthenticationService's password hashing), matching every other
/// application-service seam in this codebase.
/// </summary>
public interface IPasswordResetService
{
    /// <summary>
    /// BR-284: if email matches a real, active account, issues a time-limited single-use reset
    /// token for it and returns the raw (unhashed) value — the only time it is ever available in
    /// plaintext; only its hash is persisted (mirrors IAuthenticationService.IssueTokensAsync's own
    /// refresh-token convention). Returns null if there is no such account, so the caller (an
    /// eventual email-delivery mechanism; none exists yet, ADR/Architecture §15) has something to
    /// send — but the API controller itself must discard this return value and always answer 202
    /// either way, per BR-284's "never reveal account existence."
    /// </summary>
    Task<string?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// BR-284/BR-285: consumes a reset token to set a new password. Fails with
    /// <c>"invalid_reset_token"</c> for an unknown, expired, or already-used token, or
    /// <c>"password_too_weak"</c> for a new password that fails strength evaluation.
    /// </summary>
    Task<Result> ConfirmPasswordResetAsync(string resetToken, string newPlainTextPassword, CancellationToken cancellationToken = default);
}
