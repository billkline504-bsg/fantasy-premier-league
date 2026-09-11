using System.ComponentModel.DataAnnotations;
using EplFantasy.Identity;

namespace EplFantasy.Api.Contracts;

// Shape/format validation only (Architecture §9.3: "DTO-level validation is separate from
// domain-invariant validation") — [ApiController]'s automatic model-state check rejects a
// malformed request before it ever reaches AuthController, via IT-F14's ProblemDetails pipeline.
// Username's length/charset and Password's minimum length mirror the OpenAPI spec's Username/
// RegisterRequest schemas exactly (05-api-specification).

public sealed class RegisterRequest
{
    [Required]
    [StringLength(24, MinimumLength = 3)]
    [RegularExpression("^[A-Za-z0-9_]+$")]
    public string Username { get; set; } = null!;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = null!;

    [Required]
    [MinLength(PasswordPolicy.MinimumLength)]
    public string Password { get; set; } = null!;
}

public sealed class LoginRequest
{
    [Required]
    public string UsernameOrEmail { get; set; } = null!;

    [Required]
    public string Password { get; set; } = null!;
}

public sealed class RefreshTokenRequest
{
    [Required]
    public string RefreshToken { get; set; } = null!;
}

/// <summary>
/// Not in the OpenAPI spec as written — `/auth/logout`'s operation declares no requestBody at all,
/// even though revoking a specific refresh token (IAuthenticationService.RevokeRefreshTokenAsync)
/// requires knowing its value. Resolved by mirroring RefreshTokenRequest's shape; the spec should
/// be updated to match (done alongside this change, see the OpenAPI YAML's own inline comment).
/// </summary>
public sealed class LogoutRequest
{
    [Required]
    public string RefreshToken { get; set; } = null!;
}

public sealed class RequestPasswordResetRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = null!;
}

public sealed class ConfirmPasswordResetRequest
{
    [Required]
    public string ResetToken { get; set; } = null!;

    [Required]
    [MinLength(PasswordPolicy.MinimumLength)]
    public string NewPassword { get; set; } = null!;
}

public sealed class AuthTokenResponse
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required int ExpiresInSeconds { get; init; }
    public required UserSelfDto User { get; init; }
}

/// <summary>Self-access DTO (OpenAPI UserSelfDto) — includes fields a league-facing DTO never would (BR-015 restricts those; this is the caller's own data).</summary>
public sealed class UserSelfDto
{
    public required Guid UserId { get; init; }
    public required string Username { get; init; }
    public required string Email { get; init; }
    public required string Status { get; init; }
    public required Guid DefaultIconId { get; init; }
    public required bool IsSystemAdministrator { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
