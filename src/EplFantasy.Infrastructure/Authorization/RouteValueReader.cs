using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// ASP.NET Core's <c>AuthorizationMiddleware</c> passes the current <see cref="HttpContext"/> as
/// <see cref="AuthorizationHandlerContext.Resource"/> for a standard controller
/// <c>[Authorize(Policy = ...)]</c> check — this reads a named route value back out of it, e.g.
/// "leagueId" from a route shaped <c>/leagues/{leagueId}/...</c>. A missing or non-Guid route
/// value returns null rather than throwing: every handler here treats "couldn't determine the
/// resource id" the same as "not authorized" (fail closed), never as a server error.
/// </summary>
internal static class RouteValueReader
{
    public static Guid? GetRouteGuid(AuthorizationHandlerContext context, string routeKey)
    {
        if (context.Resource is not HttpContext httpContext)
        {
            return null;
        }

        if (!httpContext.Request.RouteValues.TryGetValue(routeKey, out var value))
        {
            return null;
        }

        return Guid.TryParse(value?.ToString(), out var guid) ? guid : null;
    }
}
