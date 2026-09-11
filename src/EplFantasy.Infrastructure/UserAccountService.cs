using EplFantasy.Identity;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so registration's User/UserProfile/UsernameHistory rows all commit in the single SaveChangesAsync below.</summary>
public sealed class UserAccountService(
    EplFantasyDbContext dbContext,
    IAuthenticationService authenticationService,
    IClock clock,
    ILogger<UserAccountService> logger) : IUserAccountService
{
    private static readonly Error UsernameTaken = new("username_taken", "That username is already taken.");
    private static readonly Error EmailTaken = new("email_taken", "That email is already registered.");
    private static readonly Error PasswordTooWeak = new(
        "password_too_weak",
        $"Password must be at least {PasswordPolicy.MinimumLength} characters and not easily guessable.");
    private static readonly Error InvalidCredentials = new("invalid_credentials", "Username/email or password is incorrect.");

    public async Task<Result<UserAccountResult>> RegisterAsync(
        string username,
        string email,
        string plainTextPassword,
        CancellationToken cancellationToken = default)
    {
        if (!PasswordPolicy.Meets(plainTextPassword, [username, email]))
        {
            return Result.Failure<UserAccountResult>(PasswordTooWeak);
        }

        // BR-004/BR-298: active users only — a retired user's username/email is free to reuse.
        // This app-level pre-check is the "domain-level check, not just a DB constraint" IT-01
        // asks for; the migration's own partial unique index (ux_users_username_active /
        // ux_users_email_active) remains the actual correctness guarantee against a concurrent
        // registration racing this same check, handled below via DbUpdateException.
        var normalizedUsername = username.ToLowerInvariant();
        var normalizedEmail = email.ToLowerInvariant();

        if (await dbContext.Users.AnyAsync(u => u.Status == UserStatus.Active && u.Username.ToLower() == normalizedUsername, cancellationToken))
        {
            return Result.Failure<UserAccountResult>(UsernameTaken);
        }

        if (await dbContext.Users.AnyAsync(u => u.Status == UserStatus.Active && u.Email.ToLower() == normalizedEmail, cancellationToken))
        {
            return Result.Failure<UserAccountResult>(EmailTaken);
        }

        var defaultIcon = await dbContext.ProfileIcons
            .Where(i => i.IsActive)
            .OrderBy(i => i.SortOrder)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No active ProfileIcon exists to assign as the default (BR-006) — the profile_icons catalog should never be empty in a correctly seeded environment.");

        var now = clock.UtcNow;
        var user = User.Register(Guid.NewGuid(), username, email, authenticationService.HashPassword(plainTextPassword), now);

        var profile = new UserProfile
        {
            UserId = user.UserId,
            DefaultIconId = defaultIcon.ProfileIconId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Users.Add(user);
        dbContext.UserProfiles.Add(profile);
        dbContext.UsernameHistories.Add(new UsernameHistory
        {
            UsernameHistoryId = Guid.NewGuid(),
            UserId = user.UserId,
            Username = username,
            EffectiveFrom = now,
            EffectiveTo = null,
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The rare race the pre-checks above can't close: two concurrent registrations for the
            // same username/email both passed the AnyAsync checks before either committed. The
            // database's partial unique index is what actually decides the winner; this just gives
            // the loser the same clean error shape as the common (pre-checked) case instead of a
            // raw constraint-violation exception reaching the API layer.
            return Result.Failure<UserAccountResult>(UsernameTaken);
        }

        logger.LogInformation("{Event}: {UserId} registered as {Username}", nameof(UserRegistered), user.UserId, user.Username);

        var tokens = await authenticationService.IssueTokensAsync(user.UserId, cancellationToken);

        return Result.Success(new UserAccountResult(user, profile, tokens));
    }

    public async Task<Result<UserAccountResult>> AuthenticateAsync(
        string usernameOrEmail,
        string plainTextPassword,
        CancellationToken cancellationToken = default)
    {
        var normalized = usernameOrEmail.ToLowerInvariant();

        var user = await dbContext.Users.SingleOrDefaultAsync(
            u => u.Status == UserStatus.Active && (u.Username.ToLower() == normalized || u.Email.ToLower() == normalized),
            cancellationToken);

        // Deliberately the same error, same code, for "no such account" and "wrong password" —
        // distinguishing them would let a caller enumerate valid usernames/emails.
        if (user is null || !authenticationService.VerifyPassword(plainTextPassword, user.PasswordHash))
        {
            return Result.Failure<UserAccountResult>(InvalidCredentials);
        }

        var profile = await dbContext.UserProfiles.SingleAsync(p => p.UserId == user.UserId, cancellationToken);
        var tokens = await authenticationService.IssueTokensAsync(user.UserId, cancellationToken);

        return Result.Success(new UserAccountResult(user, profile, tokens));
    }
}
