using System.Security.Cryptography;
using System.Text;
using EplFantasy.Identity;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EplFantasy.Infrastructure.Authentication;

public sealed class AuthenticationService(
    EplFantasyDbContext dbContext,
    IPasswordHasher passwordHasher,
    JwtTokenFactory jwtTokenFactory,
    IClock clock,
    IOptions<JwtOptions> options) : IAuthenticationService
{
    private static readonly Error InvalidRefreshToken = new(
        "invalid_refresh_token",
        "The refresh token is unknown, already revoked, or expired.");

    private readonly JwtOptions _jwtOptions = options.Value;

    public string HashPassword(string plainTextPassword) => passwordHasher.Hash(plainTextPassword);

    public bool VerifyPassword(string plainTextPassword, string passwordHash) =>
        passwordHasher.Verify(plainTextPassword, passwordHash);

    public async Task<AuthTokenResult> IssueTokensAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var (accessToken, accessTokenExpiresAt) = jwtTokenFactory.CreateAccessToken(userId, now);
        var (rawRefreshToken, refreshTokenHash) = GenerateRefreshToken();

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            UserId = userId,
            TokenHash = refreshTokenHash,
            IssuedAt = now,
            ExpiresAt = now.Add(_jwtOptions.RefreshTokenLifetime),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthTokenResult(userId, accessToken, accessTokenExpiresAt, rawRefreshToken);
    }

    public async Task<Result<AuthTokenResult>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var hash = HashToken(refreshToken);

        var existing = await dbContext.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (existing is null || existing.RevokedAt is not null || existing.ExpiresAt <= now)
        {
            return Result.Failure<AuthTokenResult>(InvalidRefreshToken);
        }

        // BR-159 rotation: the presented token is revoked in the same operation that issues its
        // replacement — it can never be redeemed a second time, whether by a legitimate retry or
        // a replay of a stolen value.
        existing.RevokedAt = now;

        var (accessToken, accessTokenExpiresAt) = jwtTokenFactory.CreateAccessToken(existing.UserId, now);
        var (rawRefreshToken, refreshTokenHash) = GenerateRefreshToken();

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            UserId = existing.UserId,
            TokenHash = refreshTokenHash,
            IssuedAt = now,
            ExpiresAt = now.Add(_jwtOptions.RefreshTokenLifetime),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new AuthTokenResult(existing.UserId, accessToken, accessTokenExpiresAt, rawRefreshToken));
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = HashToken(refreshToken);
        var existing = await dbContext.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is not null && existing.RevokedAt is null)
        {
            existing.RevokedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static (string RawToken, string Hash) GenerateRefreshToken()
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (rawToken, HashToken(rawToken));
    }

    // SHA-256, not an adaptive/slow hash: this token is a 256-bit cryptographically random value,
    // not a human-memorable secret, so it carries no brute-force risk the way a password does —
    // the same reasoning that applies to API-key hashing generally. BR-158's "adaptive hashing"
    // requirement is specifically about *passwords* (see BCryptPasswordHasher).
    private static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
