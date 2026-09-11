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
/// Proves IT-15's account-retirement orchestration against real Postgres: BR-013's soft delete
/// (never a physical row delete), AC-2's login rejection for a retired account (already enforced
/// by IUserAccountService.AuthenticateAsync's own Active-only filter — proven here end-to-end
/// rather than re-implemented), and BR-298's "the username becomes reusable" — the same scenario
/// db-tests/010...sql's `identity.username_reusable_after_retirement` already proves at the
/// database level, exercised here through the real service instead of a direct row mutation.
/// </summary>
public class UserRetirementServiceTests : IAsyncLifetime
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
    public async Task RetireAsync_marks_the_user_Retired_and_retains_the_row()
    {
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registered = await accountService.RegisterAsync($"user_{suffix}", $"user_{suffix}@example.com", StrongPassword);
        Assert.True(registered.IsSuccess);
        var userId = registered.Value.User.UserId;
        var retirementService = scope.ServiceProvider.GetRequiredService<IUserRetirementService>();

        await retirementService.RetireAsync(userId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var user = await verifyDb.Users.SingleAsync(u => u.UserId == userId); // the row still exists
        Assert.Equal(UserStatus.Retired, user.Status);
        Assert.Equal(_clock.UtcNow, user.RetiredAt);
    }

    [Fact]
    public async Task RetireAsync_prevents_the_retired_account_from_logging_in()
    {
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"user_{suffix}";
        var registered = await accountService.RegisterAsync(username, $"{username}@example.com", StrongPassword);
        var userId = registered.Value.User.UserId;
        var retirementService = scope.ServiceProvider.GetRequiredService<IUserRetirementService>();

        await retirementService.RetireAsync(userId);

        var loginResult = await accountService.AuthenticateAsync(username, StrongPassword);

        Assert.True(loginResult.IsFailure);
        Assert.Equal("invalid_credentials", loginResult.Error.Code);
    }

    [Fact]
    public async Task RetireAsync_frees_the_username_for_a_different_user_to_take_per_BR_298()
    {
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sharedUsername = $"Shared{suffix}";
        var firstRegistered = await accountService.RegisterAsync(sharedUsername, $"first_{suffix}@example.com", StrongPassword);
        Assert.True(firstRegistered.IsSuccess);
        var retirementService = scope.ServiceProvider.GetRequiredService<IUserRetirementService>();

        await retirementService.RetireAsync(firstRegistered.Value.User.UserId);

        var secondRegistered = await accountService.RegisterAsync(sharedUsername, $"second_{suffix}@example.com", StrongPassword);

        Assert.True(secondRegistered.IsSuccess, secondRegistered.IsFailure ? secondRegistered.Error.Message : null);
        Assert.NotEqual(firstRegistered.Value.User.UserId, secondRegistered.Value.User.UserId);
    }

    [Fact]
    public async Task RetireAsync_is_idempotent_for_an_already_retired_user()
    {
        await using var scope = _provider.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registered = await accountService.RegisterAsync($"user_{suffix}", $"user_{suffix}@example.com", StrongPassword);
        var userId = registered.Value.User.UserId;
        var retirementService = scope.ServiceProvider.GetRequiredService<IUserRetirementService>();
        await retirementService.RetireAsync(userId);

        // No exception on the second call.
        await retirementService.RetireAsync(userId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var user = await verifyDb.Users.SingleAsync(u => u.UserId == userId);
        Assert.Equal(UserStatus.Retired, user.Status);
    }
}
