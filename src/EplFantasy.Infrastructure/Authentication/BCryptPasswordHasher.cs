namespace EplFantasy.Infrastructure.Authentication;

/// <summary>
/// BR-158 (adaptive hashing) via bcrypt — one of the two algorithms Architecture §11 names
/// ("Argon2id or bcrypt with a modern work factor"). Chosen over Argon2id for this task because
/// BCrypt.Net-Next needs no native/unmanaged dependency, while still being adaptive (the work
/// factor is embedded in the hash and can be raised later without invalidating existing hashes —
/// BCrypt.Net verifies against whatever work factor a given hash was created with).
/// </summary>
public sealed class BCryptPasswordHasher : IPasswordHasher
{
    // 12 is bcrypt's own modern-default work factor as of BCrypt.Net-Next 4.x; revisit upward as
    // hardware improves, per BR-158's "adaptive" requirement — existing hashes stay verifiable
    // either way.
    private const int WorkFactor = 12;

    public string Hash(string plainTextPassword) => BCrypt.Net.BCrypt.HashPassword(plainTextPassword, WorkFactor);

    public bool Verify(string plainTextPassword, string passwordHash) =>
        BCrypt.Net.BCrypt.Verify(plainTextPassword, passwordHash);
}
