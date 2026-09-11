using EplFantasy.Infrastructure.Authentication;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure.Authentication;

public class BCryptPasswordHasherTests
{
    private readonly BCryptPasswordHasher _hasher = new();

    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.True(_hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void A_different_password_does_not_verify()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.False(_hasher.Verify("wrong password entirely", hash));
    }

    [Fact]
    public void Hashing_the_same_password_twice_produces_different_hashes()
    {
        // BCrypt salts each hash independently — this is what makes a leaked hash table
        // resistant to a shared rainbow-table attack across every user with the same password.
        var hashA = _hasher.Hash("correct horse battery staple");
        var hashB = _hasher.Hash("correct horse battery staple");

        Assert.NotEqual(hashA, hashB);
        Assert.True(_hasher.Verify("correct horse battery staple", hashA));
        Assert.True(_hasher.Verify("correct horse battery staple", hashB));
    }

    [Fact]
    public void The_hash_is_never_the_plaintext_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.DoesNotContain("correct horse battery staple", hash, StringComparison.Ordinal);
    }
}
