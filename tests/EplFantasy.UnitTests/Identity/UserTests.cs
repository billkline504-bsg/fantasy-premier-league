using EplFantasy.Identity;
using Xunit;

namespace EplFantasy.UnitTests.Identity;

public class UserTests
{
    [Fact]
    public void Register_establishes_the_correct_initial_state()
    {
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var user = User.Register(userId, "johnsmith", "john@example.com", "a-bcrypt-hash", now);

        Assert.Equal(userId, user.UserId);
        Assert.Equal("johnsmith", user.Username);
        Assert.Equal("john@example.com", user.Email);
        Assert.Equal("a-bcrypt-hash", user.PasswordHash);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.False(user.IsSystemAdministrator);
        Assert.Equal(now, user.CreatedAt);
        Assert.Equal(now, user.UpdatedAt);
        Assert.Null(user.RetiredAt);
    }

    [Fact]
    public void ChangeUsername_updates_the_username_and_re_stamps_updatedAt_without_touching_UserId()
    {
        var userId = Guid.NewGuid();
        var user = User.Register(userId, "johnsmith", "john@example.com", "a-bcrypt-hash", new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        user.ChangeUsername("johnsmith2", now);

        Assert.Equal(userId, user.UserId); // BR-270: identity never changes
        Assert.Equal("johnsmith2", user.Username);
        Assert.Equal(now, user.UpdatedAt);
    }

    [Fact]
    public void Retire_sets_Status_and_RetiredAt_without_touching_UserId_or_Username()
    {
        var userId = Guid.NewGuid();
        var user = User.Register(userId, "johnsmith", "john@example.com", "a-bcrypt-hash", new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        user.Retire(now);

        Assert.Equal(UserStatus.Retired, user.Status);
        Assert.Equal(now, user.RetiredAt);
        Assert.Equal(now, user.UpdatedAt);
        Assert.Equal(userId, user.UserId); // BR-013/ADR-010: never a physical delete, identity intact
        Assert.Equal("johnsmith", user.Username); // BR-014: historical records still resolve the same display name
    }

    [Fact]
    public void Retire_is_idempotent_for_an_already_retired_user()
    {
        var user = User.Register(Guid.NewGuid(), "johnsmith", "john@example.com", "a-bcrypt-hash", new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        var firstRetiredAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        user.Retire(firstRetiredAt);

        user.Retire(firstRetiredAt.AddDays(30));

        // The second call is a no-op — RetiredAt is not rewritten to the later timestamp.
        Assert.Equal(firstRetiredAt, user.RetiredAt);
    }
}

public class UsernameHistoryTests
{
    [Fact]
    public void Open_establishes_an_open_ended_row()
    {
        var usernameHistoryId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var history = UsernameHistory.Open(usernameHistoryId, userId, "johnsmith", now);

        Assert.Equal(usernameHistoryId, history.UsernameHistoryId);
        Assert.Equal(userId, history.UserId);
        Assert.Equal("johnsmith", history.Username);
        Assert.Equal(now, history.EffectiveFrom);
        Assert.Null(history.EffectiveTo);
    }

    [Fact]
    public void Close_sets_effectiveTo()
    {
        var opened = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var history = UsernameHistory.Open(Guid.NewGuid(), Guid.NewGuid(), "johnsmith", opened);
        var closed = opened.AddMonths(1);

        history.Close(closed);

        Assert.Equal(closed, history.EffectiveTo);
    }
}
