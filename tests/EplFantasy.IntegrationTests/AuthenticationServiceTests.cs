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
/// Exercises IAuthenticationService (IT-F05) through its real DI wiring (AddInfrastructure +
/// AddAuthenticationInfrastructure), against the real physical schema — not a hand-substituted
/// stand-in — with a FakeClock swapped in afterward so rotation/expiry are deterministic.
/// </summary>
public class AuthenticationServiceTests : IAsyncLifetime
{
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
            })
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(_container.GetConnectionString());
        services.AddAuthenticationInfrastructure(configuration);
        services.AddSingleton<IClock>(_clock); // shadows AddInfrastructure's SystemClock registration for deterministic tests.

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
        var files = Directory.GetFiles(migrationsDir, "V*.sql").OrderBy(f => f, StringComparer.Ordinal);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var file in files)
        {
            await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(file), connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<Guid> SeedUserAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var user = new User
        {
            UserId = Guid.NewGuid(),
            Username = $"auth_smoke_{Guid.NewGuid():N}",
            Email = $"auth_smoke_{Guid.NewGuid():N}@example.com",
            PasswordHash = "unused-in-this-test",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    [Fact]
    public async Task IssueTokensAsync_persists_a_refresh_token_row_for_the_user()
    {
        var userId = await SeedUserAsync();

        await using var scope = _provider.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

        var tokens = await auth.IssueTokensAsync(userId);

        Assert.NotEmpty(tokens.AccessToken);
        Assert.NotEmpty(tokens.RefreshToken);

        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var stored = await db.RefreshTokens.SingleAsync(t => t.UserId == userId);
        Assert.Null(stored.RevokedAt);
        Assert.DoesNotContain(tokens.RefreshToken, stored.TokenHash, StringComparison.Ordinal); // never the raw value.
    }

    [Fact]
    public async Task RefreshAsync_rotates_the_token_and_the_old_one_can_never_be_used_again()
    {
        var userId = await SeedUserAsync();

        await using var scope = _provider.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

        var original = await auth.IssueTokensAsync(userId);

        _clock.AdvanceBy(TimeSpan.FromMinutes(1));
        var refreshed = await auth.RefreshAsync(original.RefreshToken);

        Assert.True(refreshed.IsSuccess);
        Assert.NotEqual(original.RefreshToken, refreshed.Value.RefreshToken);
        Assert.NotEqual(original.AccessToken, refreshed.Value.AccessToken);

        // The original refresh token is now revoked — a second attempt to redeem it must fail,
        // whether that's a legitimate double-submit or a replay of a stolen value (BR-159).
        var secondAttempt = await auth.RefreshAsync(original.RefreshToken);
        Assert.True(secondAttempt.IsFailure);
        Assert.Equal("invalid_refresh_token", secondAttempt.Error.Code);
    }

    [Fact]
    public async Task RefreshAsync_rejects_an_unknown_token()
    {
        await using var scope = _provider.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

        var result = await auth.RefreshAsync("this-token-was-never-issued");

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_refresh_token", result.Error.Code);
    }

    [Fact]
    public async Task RefreshAsync_rejects_an_expired_token()
    {
        var userId = await SeedUserAsync();

        await using var scope = _provider.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

        var tokens = await auth.IssueTokensAsync(userId);

        _clock.AdvanceBy(TimeSpan.FromDays(31)); // past the 30-day default RefreshTokenLifetime.

        var result = await auth.RefreshAsync(tokens.RefreshToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_refresh_token", result.Error.Code);
    }

    [Fact]
    public async Task RevokeRefreshTokenAsync_prevents_further_use_and_is_idempotent()
    {
        var userId = await SeedUserAsync();

        await using var scope = _provider.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

        var tokens = await auth.IssueTokensAsync(userId);

        await auth.RevokeRefreshTokenAsync(tokens.RefreshToken);
        await auth.RevokeRefreshTokenAsync(tokens.RefreshToken); // must not throw — logout is idempotent.

        var result = await auth.RefreshAsync(tokens.RefreshToken);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Password_hash_and_verify_round_trip_through_the_service()
    {
        // IAuthenticationService itself has no DB/clock dependency for these two members —
        // exercised here anyway since it's the same public surface application code will call.
        using var scope = _provider.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

        var hash = auth.HashPassword("correct horse battery staple");

        Assert.True(auth.VerifyPassword("correct horse battery staple", hash));
        Assert.False(auth.VerifyPassword("wrong password", hash));
    }
}
