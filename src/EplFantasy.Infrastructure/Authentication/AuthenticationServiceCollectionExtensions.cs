using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EplFantasy.Infrastructure.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>
    /// Registers password hashing, JWT issuance, and <see cref="IAuthenticationService"/> against
    /// the "Jwt" configuration section. Named "...Infrastructure" rather than "AddAuthentication"
    /// specifically to avoid colliding with ASP.NET Core's own <c>AddAuthentication()</c>
    /// (Microsoft.AspNetCore.Authentication), which configures the *incoming request* auth scheme
    /// in EplFantasy.Api's Program.cs — a separate, complementary piece of wiring, not this one.
    /// </summary>
    public static IServiceCollection AddAuthenticationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<JwtTokenFactory>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();

        // IT-13: needs IAuthenticationService (registered just above) for password hashing.
        services.Configure<PasswordResetOptions>(configuration.GetSection(PasswordResetOptions.SectionName));
        services.AddScoped<IPasswordResetService, PasswordResetService>();

        return services;
    }
}
