using System.Security.Cryptography;
using System.Text;
using EplFantasy.Identity;
using EplFantasy.Infrastructure.Authentication;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see AuthenticationServiceCollectionExtensions.AddAuthenticationInfrastructure) — the same lifetime as EplFantasyDbContext, so each method's write commits in a single SaveChangesAsync().</summary>
public sealed class PasswordResetService(
    EplFantasyDbContext dbContext,
    IAuthenticationService authenticationService,
    IClock clock,
    IOptions<PasswordResetOptions> options) : IPasswordResetService
{
    private static readonly Error InvalidResetToken = new(
        "invalid_reset_token",
        "The password reset token is unknown, expired, or already used.");

    private static readonly Error PasswordTooWeak = new(
        "password_too_weak",
        $"Password must be at least {PasswordPolicy.MinimumLength} characters and not easily guessable.");

    private readonly PasswordResetOptions _options = options.Value;

    public async Task<string?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.ToLowerInvariant();
        var user = await dbContext.Users.SingleOrDefaultAsync(
            u => u.Status == UserStatus.Active && u.Email.ToLower() == normalizedEmail,
            cancellationToken);

        if (user is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        var (rawToken, tokenHash) = GenerateToken();

        dbContext.PasswordResetTokens.Add(new PasswordResetToken
        {
            PasswordResetTokenId = Guid.NewGuid(),
            UserId = user.UserId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now.Add(_options.TokenLifetime),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return rawToken;
    }

    public async Task<Result> ConfirmPasswordResetAsync(string resetToken, string newPlainTextPassword, CancellationToken cancellationToken = default)
    {
        var hash = HashToken(resetToken);
        var now = clock.UtcNow;
        var existing = await dbContext.PasswordResetTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is null || !existing.IsValid(now))
        {
            return Result.Failure(InvalidResetToken);
        }

        var user = await dbContext.Users.SingleAsync(u => u.UserId == existing.UserId, cancellationToken);

        if (!PasswordPolicy.Meets(newPlainTextPassword, [user.Username, user.Email]))
        {
            return Result.Failure(PasswordTooWeak);
        }

        // Defensive re-check immediately before persisting — see MarkUsed's own remarks on why
        // this isn't redundant with the IsValid guard above.
        existing.MarkUsed(now);

        user.PasswordHash = authenticationService.HashPassword(newPlainTextPassword);
        user.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    // Mirrors AuthenticationService's own GenerateRefreshToken/HashToken exactly — see that
    // class's remarks on why SHA-256 (not an adaptive/slow hash) is the right choice for a
    // 256-bit cryptographically random token, as opposed to BCryptPasswordHasher's use for
    // actual user-chosen passwords.
    private static (string RawToken, string Hash) GenerateToken()
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (rawToken, HashToken(rawToken));
    }

    private static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
