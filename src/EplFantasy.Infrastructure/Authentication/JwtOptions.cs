namespace EplFantasy.Infrastructure.Authentication;

/// <summary>
/// Bound from configuration (the "Jwt" section). <see cref="SigningKey"/> must come from
/// environment/secret-store injection at deploy time (BR-173) — never a literal in an
/// appsettings.*.json that gets committed. appsettings.Development.json's value is a local-only
/// placeholder for exactly that reason.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SigningKey { get; init; }
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);
}
