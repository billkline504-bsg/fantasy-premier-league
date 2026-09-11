using EplFantasy.Api.Contracts;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-01 (F-001.1): the four auth endpoints (Architecture §9.1, OpenAPI Specification v1.0's
/// Identity tag). Every action is decorated [EnableRateLimiting("auth")] per Architecture §11's
/// "/auth/* endpoints" (BR-169) — the first real consumer of the policy IT-F14 built. IT-13
/// (F-001.2) adds the password-reset request/confirm pair, both anonymous and abuse-prone in
/// exactly the same way, so they inherit this same rate-limiting policy.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public class AuthController(
    IUserAccountService accountService,
    IAuthenticationService authenticationService,
    IPasswordResetService passwordResetService,
    EplFantasyDbContext dbContext,
    IClock clock) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await accountService.RegisterAsync(request.Username, request.Email, request.Password, cancellationToken);

        if (result.IsFailure)
        {
            var statusCode = result.Error.Code is "username_taken" or "email_taken"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return ToProblem(result.Error, statusCode);
        }

        return StatusCode(StatusCodes.Status201Created, ToAuthTokenResponse(result.Value));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await accountService.AuthenticateAsync(request.UsernameOrEmail, request.Password, cancellationToken);

        return result.IsFailure
            ? ToProblem(result.Error, StatusCodes.Status401Unauthorized)
            : Ok(ToAuthTokenResponse(result.Value));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await authenticationService.RefreshAsync(request.RefreshToken, cancellationToken);
        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status401Unauthorized);
        }

        var tokens = result.Value;
        var user = await dbContext.Users.SingleAsync(u => u.UserId == tokens.UserId, cancellationToken);
        var profile = await dbContext.UserProfiles.SingleAsync(p => p.UserId == tokens.UserId, cancellationToken);

        return Ok(ToAuthTokenResponse(new UserAccountResult(user, profile, tokens)));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        await authenticationService.RevokeRefreshTokenAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    [HttpPost("password-reset/request")]
    public async Task<IActionResult> RequestPasswordReset(RequestPasswordResetRequest request, CancellationToken cancellationToken)
    {
        // BR-284: the return value (whether a real account matched) is deliberately discarded here
        // — an eventual email-delivery mechanism would consume it (see IPasswordResetService's own
        // remarks), but the API itself must answer identically either way.
        await passwordResetService.RequestPasswordResetAsync(request.Email, cancellationToken);

        return Accepted();
    }

    [HttpPost("password-reset/confirm")]
    public async Task<IActionResult> ConfirmPasswordReset(ConfirmPasswordResetRequest request, CancellationToken cancellationToken)
    {
        var result = await passwordResetService.ConfirmPasswordResetAsync(request.ResetToken, request.NewPassword, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status400BadRequest);
        }

        return NoContent();
    }

    // ControllerBase.Problem() has no `extensions` parameter to set errorCode directly — its
    // result still goes through ProblemDetailsServiceCollectionExtensions' CustomizeProblemDetails
    // hook (correlationId, a status-code-based errorCode fallback), so the fix is to call it
    // normally and then overwrite the fallback errorCode with this failure's real one.
    private ObjectResult ToProblem(Error error, int statusCode)
    {
        var problem = Problem(detail: error.Message, statusCode: statusCode);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = error.Code;
        return problem;
    }

    private AuthTokenResponse ToAuthTokenResponse(UserAccountResult account) => new()
    {
        AccessToken = account.Tokens.AccessToken,
        RefreshToken = account.Tokens.RefreshToken,
        ExpiresInSeconds = (int)(account.Tokens.AccessTokenExpiresAt - clock.UtcNow).TotalSeconds,
        User = new UserSelfDto
        {
            UserId = account.User.UserId,
            Username = account.User.Username,
            Email = account.User.Email,
            Status = account.User.Status.ToString(),
            DefaultIconId = account.Profile.DefaultIconId,
            IsSystemAdministrator = account.User.IsSystemAdministrator,
            CreatedAt = account.User.CreatedAt,
        },
    };
}
