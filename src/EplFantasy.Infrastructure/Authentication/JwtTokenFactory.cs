using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EplFantasy.Infrastructure.Authentication;

/// <summary>
/// Pure JWT creation — no database or clock dependency of its own (the caller supplies "now",
/// keeping this class trivially unit-testable and avoiding a second, redundant IClock injection
/// alongside AuthenticationService's). Validation of incoming tokens is configured separately, in
/// EplFantasy.Api's Program.cs, against the same <see cref="JwtOptions"/> — this factory only
/// issues.
/// </summary>
public sealed class JwtTokenFactory(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>ADR-007: the token carries only <c>sub</c> (UserId) — never roles/permissions, since League Administrator status is per-league and must be checked live.</summary>
    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Guid userId, DateTimeOffset now)
    {
        var expiresAt = now.Add(_options.AccessTokenLifetime);

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()) };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
