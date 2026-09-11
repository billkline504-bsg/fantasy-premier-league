using EplFantasy.Identity;
using Xunit;

namespace EplFantasy.UnitTests.Identity;

public class PasswordResetTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static PasswordResetToken NewToken(TimeSpan lifetime) => new()
    {
        PasswordResetTokenId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TokenHash = "irrelevant-hash",
        CreatedAt = Now,
        ExpiresAt = Now.Add(lifetime),
    };

    [Fact]
    public void IsValid_is_true_before_expiry_for_an_unused_token()
    {
        var token = NewToken(TimeSpan.FromHours(1));

        Assert.True(token.IsValid(Now.AddMinutes(30)));
    }

    [Fact]
    public void IsValid_is_false_once_past_expiresAt()
    {
        var token = NewToken(TimeSpan.FromHours(1));

        Assert.False(token.IsValid(Now.AddHours(1).AddSeconds(1)));
    }

    [Fact]
    public void IsValid_is_false_once_used_even_before_expiry()
    {
        var token = NewToken(TimeSpan.FromHours(1));
        token.MarkUsed(Now.AddMinutes(10));

        Assert.False(token.IsValid(Now.AddMinutes(20)));
    }

    [Fact]
    public void MarkUsed_sets_usedAt_for_a_valid_token()
    {
        var token = NewToken(TimeSpan.FromHours(1));

        token.MarkUsed(Now.AddMinutes(10));

        Assert.Equal(Now.AddMinutes(10), token.UsedAt);
    }

    [Fact]
    public void MarkUsed_throws_for_an_expired_token_and_leaves_it_unused()
    {
        var token = NewToken(TimeSpan.FromHours(1));

        var exception = Assert.Throws<PasswordResetTokenInvalidException>(() => token.MarkUsed(Now.AddHours(2)));

        Assert.Equal("invalid_reset_token", exception.ErrorCode);
        Assert.Equal(400, exception.StatusCode);
        Assert.Null(token.UsedAt);
    }

    [Fact]
    public void MarkUsed_throws_for_an_already_used_token()
    {
        var token = NewToken(TimeSpan.FromHours(1));
        token.MarkUsed(Now.AddMinutes(5));

        var exception = Assert.Throws<PasswordResetTokenInvalidException>(() => token.MarkUsed(Now.AddMinutes(10)));

        Assert.Equal("invalid_reset_token", exception.ErrorCode);
        Assert.Equal(Now.AddMinutes(5), token.UsedAt); // unchanged by the rejected second call
    }
}
