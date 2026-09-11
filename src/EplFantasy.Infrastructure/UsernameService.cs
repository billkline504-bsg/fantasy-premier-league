using EplFantasy.Identity;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so the User update and the UsernameHistory rollover commit in a single SaveChangesAsync().</summary>
public sealed class UsernameService(EplFantasyDbContext dbContext, IClock clock, ILogger<UsernameService> logger) : IUsernameService
{
    private static readonly Error UsernameTaken = new("username_taken", "That username is already taken.");

    public async Task<Result<User>> ChangeUsernameAsync(Guid userId, string newUsername, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.SingleAsync(u => u.UserId == userId, cancellationToken);

        // Identical, including case, to the current username — a genuine no-op; BR-326 shouldn't
        // open a redundant UsernameHistory row for a change that didn't actually happen. A pure
        // case change (e.g. "JohnSmith" -> "JOHNSMITH") is NOT a no-op — it's a real, displayable
        // change — so this compares Ordinal, not case-insensitively.
        if (string.Equals(user.Username, newUsername, StringComparison.Ordinal))
        {
            return Result.Success(user);
        }

        // BR-004/BR-298: active users only, case-insensitive — the same scope
        // UserAccountService.RegisterAsync's own pre-check uses. Excludes the caller's own row so
        // a pure case change never collides with itself.
        var normalizedNewUsername = newUsername.ToLowerInvariant();
        if (await dbContext.Users.AnyAsync(
            u => u.UserId != userId && u.Status == UserStatus.Active && u.Username.ToLower() == normalizedNewUsername,
            cancellationToken))
        {
            return Result.Failure<User>(UsernameTaken);
        }

        var now = clock.UtcNow;
        var oldUsername = user.Username;

        var currentHistory = await dbContext.UsernameHistories.SingleAsync(
            h => h.UserId == userId && h.EffectiveTo == null,
            cancellationToken);
        currentHistory.Close(now);
        dbContext.UsernameHistories.Add(UsernameHistory.Open(Guid.NewGuid(), userId, newUsername, now));

        user.ChangeUsername(newUsername, now);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The rare race the pre-check above can't close: two concurrent renames to the same
            // username both passed the AnyAsync check before either committed — the same
            // second-layer-of-defense pattern RegisterAsync's own DbUpdateException handling uses.
            return Result.Failure<User>(UsernameTaken);
        }

        logger.LogInformation(
            "{Event}: {UserId} renamed from {OldUsername} to {NewUsername}",
            nameof(UsernameChanged),
            userId,
            oldUsername,
            newUsername);

        return Result.Success(user);
    }
}
