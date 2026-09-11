using EplFantasy.Api.Contracts;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// The caller's own User/UserProfile (OpenAPI Specification v1.0's getCurrentUser), username
/// change (IT-14, F-001.3's updateUsername), and account retirement (IT-15, F-001.4's
/// retireCurrentUser) — all under `/users/me`. getCurrentUser itself isn't named by any
/// Implementation Task Breakdown entry (a documentation gap, the same kind
/// AuthController.Logout's request body already worked around) — it shares updateUsername's exact
/// route and needs no domain logic of its own, so it's built alongside it here rather than left
/// permanently unbuilt. reactivateUser (System-Administrator-only, a *different* route and a
/// meaningfully more complex username-conflict rule per Feature Behavior Spec F-001.4 AC-7) is
/// deliberately NOT built here — IT-15's own scope names only retireCurrentUser.
/// </summary>
[ApiController]
[Route("api/v1/users/me")]
[Authorize]
public class UserController(
    IUsernameService usernameService,
    IUserRetirementService userRetirementService,
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;
        var user = await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == userId, cancellationToken);
        var profile = await dbContext.UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == userId, cancellationToken);

        return Ok(ToDto(user, profile));
    }

    [HttpPut]
    public async Task<IActionResult> UpdateUsername(UpdateUsernameRequest request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;
        var result = await usernameService.ChangeUsernameAsync(userId, request.Username, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status409Conflict);
        }

        var profile = await dbContext.UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == userId, cancellationToken);

        return Ok(ToDto(result.Value, profile));
    }

    [HttpPost("retire")]
    public async Task<IActionResult> RetireCurrentUser(CancellationToken cancellationToken)
    {
        await userRetirementService.RetireAsync(currentUser.UserId!.Value, cancellationToken);

        return NoContent();
    }

    // Mirrors AuthController.ToProblem — see that method's own remarks on why the fallback
    // errorCode has to be overwritten explicitly rather than passed to Problem() directly.
    private ObjectResult ToProblem(Error error, int statusCode)
    {
        var problem = Problem(detail: error.Message, statusCode: statusCode);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = error.Code;
        return problem;
    }

    private static UserSelfDto ToDto(User user, UserProfile profile) => new()
    {
        UserId = user.UserId,
        Username = user.Username,
        Email = user.Email,
        Status = user.Status.ToString(),
        DefaultIconId = profile.DefaultIconId,
        IsSystemAdministrator = user.IsSystemAdministrator,
        CreatedAt = user.CreatedAt,
    };
}
