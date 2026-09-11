namespace EplFantasy.Infrastructure.Authentication;

/// <summary>
/// Not one of ADR-006's named boundary interfaces (IAuthenticationService/ICurrentUserAccessor) —
/// a smaller supporting abstraction so password hashing has a single-responsibility seam of its
/// own, testable independently of token issuance/refresh-token persistence.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>BR-158: adaptive hash only, never reversible/plaintext.</summary>
    string Hash(string plainTextPassword);

    bool Verify(string plainTextPassword, string passwordHash);
}
