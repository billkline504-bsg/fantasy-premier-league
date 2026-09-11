using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authentication;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-13's password-reset orchestration against real Postgres: BR-284's "never reveal
/// account existence," time-limited single-use token, and BR-285's strength check on the new
/// password.
/// </summary>
public class PasswordResetServiceTests : IAsyncLifetime
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "integration-tests",
                ["Jwt:Audience"] = "integration-tests",
                ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-bytes-long",
                ["PasswordReset:TokenLifetime"] = "01:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(_container.GetConnectionString());
        services.AddAuthenticationInfrastructure(configuration);
        services.AddSingleton<IClock>(_clock);
        _provider = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static async Task ApplyHandWrittenMigrationsAsync(string connectionString)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EplFantasy.sln")))
        {
            dir = dir.Parent;
        }

        var migrationsDir = Path.Combine(dir!.FullName, "docs", "aidlc", "06-database-migrations", "migrations");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var file in Directory.GetFiles(migrationsDir, "V*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(file), connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<(Guid UserId, string Username, string Email)> SeedUserAsync(EplFantasyDbContext db, IAuthenticationService authenticationService)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"user_{suffix}";
        var email = $"user_{suffix}@example.com";
        var user = new User { UserId = Guid.NewGuid(), Username = username, Email = email, PasswordHash = authenticationService.HashPassword(StrongPassword), CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.UserId, username, email);
    }

    [Fact]
    public async Task RequestPasswordResetAsync_issues_a_token_for_a_real_active_account()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var authenticationService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var (userId, _, email) = await SeedUserAsync(db, authenticationService);
        var service = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();

        var rawToken = await service.RequestPasswordResetAsync(email);

        Assert.False(string.IsNullOrWhiteSpace(rawToken));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var token = await verifyDb.PasswordResetTokens.SingleAsync(t => t.UserId == userId);
        Assert.Equal(_clock.UtcNow, token.CreatedAt);
        Assert.Equal(_clock.UtcNow.AddHours(1), token.ExpiresAt);
        Assert.Null(token.UsedAt);
        Assert.NotEqual(rawToken, token.TokenHash); // only the hash is persisted
    }

    [Fact]
    public async Task RequestPasswordResetAsync_returns_null_for_an_email_that_matches_no_account_and_writes_no_row()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();

        var rawToken = await service.RequestPasswordResetAsync($"nobody-{Guid.NewGuid():N}@example.com");

        Assert.Null(rawToken);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.Equal(0, await verifyDb.PasswordResetTokens.CountAsync());
    }

    [Fact]
    public async Task ConfirmPasswordResetAsync_changes_the_password_and_allows_login_with_the_new_one()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var authenticationService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var (userId, _, email) = await SeedUserAsync(db, authenticationService);
        var passwordResetService = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();
        var rawToken = await passwordResetService.RequestPasswordResetAsync(email);

        const string newPassword = "a-brand-new-strong-password-42!";
        var result = await passwordResetService.ConfirmPasswordResetAsync(rawToken!, newPassword);

        Assert.True(result.IsSuccess);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var verifyAuth = verifyScope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var user = await verifyDb.Users.SingleAsync(u => u.UserId == userId);
        Assert.True(verifyAuth.VerifyPassword(newPassword, user.PasswordHash));
        Assert.False(verifyAuth.VerifyPassword(StrongPassword, user.PasswordHash));

        var token = await verifyDb.PasswordResetTokens.SingleAsync(t => t.UserId == userId);
        Assert.Equal(_clock.UtcNow, token.UsedAt);
    }

    [Fact]
    public async Task ConfirmPasswordResetAsync_rejects_an_unknown_token()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();

        var result = await service.ConfirmPasswordResetAsync("this-token-was-never-issued", "a-brand-new-strong-password-42!");

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_reset_token", result.Error.Code);
    }

    [Fact]
    public async Task ConfirmPasswordResetAsync_rejects_an_expired_token_and_leaves_the_password_unchanged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var authenticationService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var (userId, _, email) = await SeedUserAsync(db, authenticationService);
        var passwordResetService = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();
        var rawToken = await passwordResetService.RequestPasswordResetAsync(email);

        _clock.AdvanceBy(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));
        var result = await passwordResetService.ConfirmPasswordResetAsync(rawToken!, "a-brand-new-strong-password-42!");

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_reset_token", result.Error.Code);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var verifyAuth = verifyScope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var user = await verifyDb.Users.SingleAsync(u => u.UserId == userId);
        Assert.True(verifyAuth.VerifyPassword(StrongPassword, user.PasswordHash)); // unchanged
    }

    [Fact]
    public async Task ConfirmPasswordResetAsync_rejects_reusing_an_already_used_token()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var authenticationService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var (_, _, email) = await SeedUserAsync(db, authenticationService);
        var passwordResetService = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();
        var rawToken = await passwordResetService.RequestPasswordResetAsync(email);
        await passwordResetService.ConfirmPasswordResetAsync(rawToken!, "a-brand-new-strong-password-42!");

        var result = await passwordResetService.ConfirmPasswordResetAsync(rawToken!, "yet-another-strong-password-43!");

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_reset_token", result.Error.Code);
    }

    [Fact]
    public async Task ConfirmPasswordResetAsync_rejects_a_weak_new_password_and_does_not_consume_the_token()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var authenticationService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var (userId, _, email) = await SeedUserAsync(db, authenticationService);
        var passwordResetService = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();
        var rawToken = await passwordResetService.RequestPasswordResetAsync(email);

        var weakResult = await passwordResetService.ConfirmPasswordResetAsync(rawToken!, "aaaaaaaaaaaa");

        Assert.True(weakResult.IsFailure);
        Assert.Equal("password_too_weak", weakResult.Error.Code);

        // The token survives a rejected-for-weak-password attempt — the user can retry with a
        // stronger password using the same link, rather than being forced to request a new one.
        var secondResult = await passwordResetService.ConfirmPasswordResetAsync(rawToken!, "a-brand-new-strong-password-42!");
        Assert.True(secondResult.IsSuccess);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var verifyAuth = verifyScope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var user = await verifyDb.Users.SingleAsync(u => u.UserId == userId);
        Assert.True(verifyAuth.VerifyPassword("a-brand-new-strong-password-42!", user.PasswordHash));
    }
}
