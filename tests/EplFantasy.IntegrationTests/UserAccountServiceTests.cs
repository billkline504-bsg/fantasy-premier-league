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
/// Proves IT-01's registration/login claims against real Postgres: BR-004/BR-298 uniqueness scoped
/// to active users, BR-285 password strength rejected before any row is written, BR-326's opened
/// UsernameHistory row, BR-006's default ProfileIcon assignment, and BR-156 login by username or
/// email.
/// </summary>
public class UserAccountServiceTests : IAsyncLifetime
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

    [Fact]
    public async Task RegisterAsync_creates_a_full_account_and_issues_tokens()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();

        var result = await accountService.RegisterAsync($"user{suffix}", $"user{suffix}@example.com", StrongPassword);

        Assert.True(result.IsSuccess);
        var account = result.Value;
        Assert.Equal(UserStatus.Active, account.User.Status);
        Assert.NotEqual(StrongPassword, account.User.PasswordHash);
        Assert.NotEmpty(account.Tokens.AccessToken);
        Assert.NotEmpty(account.Tokens.RefreshToken);
        Assert.Equal(account.User.UserId, account.Tokens.UserId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var profile = await db.UserProfiles.SingleAsync(p => p.UserId == account.User.UserId);
        Assert.Equal(account.Profile.DefaultIconId, profile.DefaultIconId);
        var iconExists = await db.ProfileIcons.AnyAsync(i => i.ProfileIconId == profile.DefaultIconId && i.IsActive);
        Assert.True(iconExists, "the assigned default icon must be a real, active catalog entry (BR-006)");

        var history = await db.UsernameHistories.SingleAsync(h => h.UserId == account.User.UserId);
        Assert.Equal(_clock.UtcNow, history.EffectiveFrom);
        Assert.Null(history.EffectiveTo);

        var refreshTokenExists = await db.RefreshTokens.AnyAsync(t => t.UserId == account.User.UserId && t.RevokedAt == null);
        Assert.True(refreshTokenExists);
    }

    [Fact]
    public async Task RegisterAsync_rejects_a_duplicate_active_username_case_insensitively()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"Dup{suffix}";
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();

        var first = await accountService.RegisterAsync(username, $"first{suffix}@example.com", StrongPassword);
        Assert.True(first.IsSuccess);

        var second = await accountService.RegisterAsync(username.ToUpperInvariant(), $"second{suffix}@example.com", StrongPassword);

        Assert.True(second.IsFailure);
        Assert.Equal("username_taken", second.Error.Code);

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var count = await db.Users.CountAsync(u => u.Email == $"second{suffix}@example.com");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RegisterAsync_rejects_a_duplicate_active_email_case_insensitively()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"Shared{suffix}@Example.com";
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();

        var first = await accountService.RegisterAsync($"first{suffix}", email, StrongPassword);
        Assert.True(first.IsSuccess);

        var second = await accountService.RegisterAsync($"second{suffix}", email.ToLowerInvariant(), StrongPassword);

        Assert.True(second.IsFailure);
        Assert.Equal("email_taken", second.Error.Code);
    }

    [Fact]
    public async Task RegisterAsync_allows_reusing_a_username_freed_by_retirement_per_BR_298()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"Recycle{suffix}";
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var first = await accountService.RegisterAsync(username, $"original{suffix}@example.com", StrongPassword);
        Assert.True(first.IsSuccess);

        var retiredUser = await db.Users.SingleAsync(u => u.UserId == first.Value.User.UserId);
        retiredUser.Status = UserStatus.Retired;
        retiredUser.RetiredAt = _clock.UtcNow;
        await db.SaveChangesAsync();

        var second = await accountService.RegisterAsync(username, $"newowner{suffix}@example.com", StrongPassword);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);
    }

    [Fact]
    public async Task RegisterAsync_rejects_a_weak_password_before_writing_any_row()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();

        var result = await accountService.RegisterAsync($"user{suffix}", $"user{suffix}@example.com", "aaaaaaaaaaaa");

        Assert.True(result.IsFailure);
        Assert.Equal("password_too_weak", result.Error.Code);

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Username == $"user{suffix}"));
    }

    [Fact]
    public async Task AuthenticateAsync_succeeds_with_username_or_email_case_insensitively()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"Login{suffix}";
        var email = $"Login{suffix}@Example.com";
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();

        var registered = await accountService.RegisterAsync(username, email, StrongPassword);
        Assert.True(registered.IsSuccess);

        var byUsername = await accountService.AuthenticateAsync(username.ToLowerInvariant(), StrongPassword);
        Assert.True(byUsername.IsSuccess);
        Assert.Equal(registered.Value.User.UserId, byUsername.Value.User.UserId);

        var byEmail = await accountService.AuthenticateAsync(email.ToUpperInvariant(), StrongPassword);
        Assert.True(byEmail.IsSuccess);
        Assert.Equal(registered.Value.User.UserId, byEmail.Value.User.UserId);
    }

    [Fact]
    public async Task AuthenticateAsync_fails_with_the_same_error_for_unknown_identifier_and_wrong_password()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();

        var registered = await accountService.RegisterAsync($"user{suffix}", $"user{suffix}@example.com", StrongPassword);
        Assert.True(registered.IsSuccess);

        var wrongPassword = await accountService.AuthenticateAsync($"user{suffix}", "wrong-password-entirely");
        var unknownUser = await accountService.AuthenticateAsync($"nobody-{suffix}", StrongPassword);

        Assert.True(wrongPassword.IsFailure);
        Assert.True(unknownUser.IsFailure);
        Assert.Equal("invalid_credentials", wrongPassword.Error.Code);
        Assert.Equal(wrongPassword.Error.Code, unknownUser.Error.Code);
    }

    [Fact]
    public async Task AuthenticateAsync_fails_for_a_retired_user_even_with_the_correct_password()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var registered = await accountService.RegisterAsync($"user{suffix}", $"user{suffix}@example.com", StrongPassword);
        Assert.True(registered.IsSuccess);

        var user = await db.Users.SingleAsync(u => u.UserId == registered.Value.User.UserId);
        user.Status = UserStatus.Retired;
        user.RetiredAt = _clock.UtcNow;
        await db.SaveChangesAsync();

        var result = await accountService.AuthenticateAsync($"user{suffix}", StrongPassword);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_credentials", result.Error.Code);
    }
}
