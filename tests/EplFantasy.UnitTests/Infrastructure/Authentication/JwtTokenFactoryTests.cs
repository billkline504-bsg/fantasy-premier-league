using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EplFantasy.Infrastructure.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure.Authentication;

public class JwtTokenFactoryTests
{
    private static readonly JwtOptions Options = new()
    {
        Issuer = "eplfantasy-tests",
        Audience = "eplfantasy-tests",
        SigningKey = "unit-test-signing-key-at-least-32-bytes-long-for-hs256",
        AccessTokenLifetime = TimeSpan.FromMinutes(15),
    };

    private readonly JwtTokenFactory _factory = new(Microsoft.Extensions.Options.Options.Create(Options));

    [Fact]
    public void The_token_carries_only_the_sub_claim_and_no_role_or_permission_claims()
    {
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var (token, _) = _factory.CreateAccessToken(userId, now);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        // ADR-007: never roles/permissions as claims — League Administrator status must be
        // checked live against current data, not cached in the token.
        Assert.Equal(userId.ToString(), jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "role" or ClaimTypes.Role);
    }

    [Fact]
    public void The_token_expires_exactly_at_now_plus_the_configured_lifetime()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var (_, expiresAt) = _factory.CreateAccessToken(Guid.NewGuid(), now);

        Assert.Equal(now + Options.AccessTokenLifetime, expiresAt);
    }

    [Fact]
    public void The_token_validates_successfully_against_the_matching_signing_key_issuer_and_audience()
    {
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var (token, _) = _factory.CreateAccessToken(userId, now);

        // MapInboundClaims = false, matching Program.cs's JwtSecurityTokenHandler.DefaultMapInboundClaims
        // setting for the real app — otherwise this handler instance silently renames "sub" to the
        // long ClaimTypes.NameIdentifier URI on validation, and the assertion below would find nothing.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Options.Issuer,
            ValidateAudience = true,
            ValidAudience = Options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Options.SigningKey)),
            ValidateLifetime = true,
        }, out _);

        Assert.Equal(userId.ToString(), principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);
    }

    [Fact]
    public void The_token_fails_validation_against_a_different_signing_key()
    {
        var (token, _) = _factory.CreateAccessToken(Guid.NewGuid(), DateTimeOffset.UtcNow);

        var handler = new JwtSecurityTokenHandler();
        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(() => handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Options.Issuer,
            ValidateAudience = true,
            ValidAudience = Options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-signing-key-of-sufficient-length")),
            ValidateLifetime = true,
        }, out _));
    }

    [Fact]
    public void An_expired_token_fails_validation()
    {
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        var (token, _) = _factory.CreateAccessToken(Guid.NewGuid(), longAgo);

        var handler = new JwtSecurityTokenHandler();
        Assert.Throws<SecurityTokenExpiredException>(() => handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Options.Issuer,
            ValidateAudience = true,
            ValidAudience = Options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Options.SigningKey)),
            ValidateLifetime = true,
        }, out _));
    }
}
