using EplFantasy.Identity;
using Xunit;

namespace EplFantasy.UnitTests.Identity;

/// <summary>BR-285: a strength-estimation mechanism (zxcvbn), not fixed composition rules.</summary>
public class PasswordPolicyTests
{
    [Fact]
    public void A_password_shorter_than_the_minimum_length_is_rejected_regardless_of_complexity()
    {
        // 11 chars, high apparent complexity — still fails purely on length.
        Assert.False(PasswordPolicy.Meets("Zx9!Qw7@Lm", []));
    }

    [Fact]
    public void A_long_but_low_entropy_password_is_rejected()
    {
        // 12+ chars, satisfies any fixed composition rule, but a textbook zxcvbn low-entropy match
        // (simple repeat) — this is exactly what BR-285 says fixed composition rules would miss.
        Assert.False(PasswordPolicy.Meets("aaaaaaaaaaaa", []));
    }

    [Fact]
    public void A_password_matching_a_common_pattern_is_rejected_even_past_the_length_floor()
    {
        Assert.False(PasswordPolicy.Meets("qwertyuiop12", []));
    }

    [Fact]
    public void A_password_built_from_the_users_own_username_or_email_is_rejected()
    {
        Assert.False(PasswordPolicy.Meets("johnsmith123456", ["johnsmith", "johnsmith@example.com"]));
    }

    [Fact]
    public void A_genuinely_strong_passphrase_is_accepted()
    {
        Assert.True(PasswordPolicy.Meets("correct-horse-battery-staple-97!", ["johnsmith", "johnsmith@example.com"]));
    }
}
