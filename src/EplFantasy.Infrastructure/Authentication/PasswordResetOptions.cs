namespace EplFantasy.Infrastructure.Authentication;

/// <summary>BR-284: how long a password reset token remains valid after issuance. Bound from the "PasswordReset" configuration section; has a sensible default so an app with no such section still works.</summary>
public sealed class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(1);
}
