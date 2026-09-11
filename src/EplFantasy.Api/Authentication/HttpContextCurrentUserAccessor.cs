using System.IdentityModel.Tokens.Jwt;
using EplFantasy.SharedKernel;

namespace EplFantasy.Api.Authentication;

/// <summary>
/// The one place <see cref="Microsoft.AspNetCore.Http.HttpContext"/> is read to answer "who is
/// logged in" (ICurrentUserAccessor's own remarks, SharedKernel) — relies on Program.cs disabling
/// JwtSecurityTokenHandler's default inbound claim-type remapping, so the "sub" claim survives
/// under its original JWT name instead of being silently renamed to a long .NET/WS-Fed URI.
/// </summary>
public sealed class HttpContextCurrentUserAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentUserAccessor
{
    public Guid? UserId
    {
        get
        {
            var subject = httpContextAccessor.HttpContext?.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            return Guid.TryParse(subject, out var userId) ? userId : null;
        }
    }
}
