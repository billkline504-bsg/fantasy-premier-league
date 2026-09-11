using EplFantasy.Api.Contracts;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-09 (F-002.1): the caller's global default profile icon, and the application-controlled icon
/// catalog it must be chosen from (OpenAPI Specification v1.0's updateDefaultIcon/listProfileIcons,
/// BR-006/BR-011).
/// </summary>
[ApiController]
public class ProfileController(
    IProfileService profileService,
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpPut("api/v1/users/me/icon")]
    [Authorize]
    public async Task<IActionResult> UpdateDefaultIcon(UpdateDefaultIconRequest request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;
        var result = await profileService.UpdateDefaultIconAsync(userId, request.ProfileIconId!.Value, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status400BadRequest);
        }

        var user = await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == userId, cancellationToken);

        return Ok(new UserSelfDto
        {
            UserId = user.UserId,
            Username = user.Username,
            Email = user.Email,
            Status = user.Status.ToString(),
            DefaultIconId = result.Value.DefaultIconId,
            IsSystemAdministrator = user.IsSystemAdministrator,
            CreatedAt = user.CreatedAt,
        });
    }

    // Anonymous — the OpenAPI operation carries no x-authorization note; it's just a static,
    // non-sensitive reference catalog (BR-011).
    [HttpGet("api/v1/profile-icons")]
    public async Task<IActionResult> ListProfileIcons(CancellationToken cancellationToken)
    {
        var icons = await dbContext.ProfileIcons.AsNoTracking()
            .Where(i => i.IsActive)
            .OrderBy(i => i.SortOrder)
            .ToListAsync(cancellationToken);

        return Ok(icons.Select(ProfileIconDto.From).ToList());
    }

    // Mirrors AuthController.ToProblem — see that method's own remarks on why the fallback
    // errorCode has to be overwritten explicitly rather than passed to Problem() directly.
    private ObjectResult ToProblem(Error error, int statusCode)
    {
        var problem = Problem(detail: error.Message, statusCode: statusCode);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = error.Code;
        return problem;
    }
}
