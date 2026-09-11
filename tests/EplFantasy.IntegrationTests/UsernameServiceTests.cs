using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-14's username-change orchestration against real Postgres: BR-004/BR-266's
/// re-validated uniqueness, and BR-326's rollover — closing the currently-open UsernameHistory row
/// and opening a new one in the same transaction, exactly the pattern
/// db-tests/010_identity_invariants.sql's `identity.username_history_one_open_row` and
/// `identity.username_history_rollover_after_close` already prove at the database level.
/// </summary>
public class UsernameServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(_container.GetConnectionString());
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

    private async Task<Guid> SeedUserAsync(EplFantasyDbContext db, string username)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { UserId = Guid.NewGuid(), Username = username, Email = $"{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        db.UsernameHistories.Add(new UsernameHistory { UsernameHistoryId = Guid.NewGuid(), UserId = user.UserId, Username = username, EffectiveFrom = _clock.UtcNow, EffectiveTo = null });
        await db.SaveChangesAsync();
        return user.UserId;
    }

    [Fact]
    public async Task ChangeUsernameAsync_closes_the_open_history_row_and_opens_a_new_one()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = await SeedUserAsync(db, $"original_{suffix}");
        var service = scope.ServiceProvider.GetRequiredService<IUsernameService>();

        _clock.AdvanceBy(TimeSpan.FromDays(1));
        var newUsername = $"renamed_{suffix}";
        var result = await service.ChangeUsernameAsync(userId, newUsername);

        Assert.True(result.IsSuccess);
        Assert.Equal(newUsername, result.Value.Username);
        Assert.Equal(_clock.UtcNow, result.Value.UpdatedAt);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var histories = await verifyDb.UsernameHistories.Where(h => h.UserId == userId).OrderBy(h => h.EffectiveFrom).ToListAsync();
        Assert.Equal(2, histories.Count);
        Assert.Equal($"original_{suffix}", histories[0].Username);
        Assert.Equal(_clock.UtcNow, histories[0].EffectiveTo); // closed
        Assert.Equal(newUsername, histories[1].Username);
        Assert.Null(histories[1].EffectiveTo); // the new open row
        Assert.Single(histories, h => h.EffectiveTo == null); // ux_username_history_open still satisfied
    }

    [Fact]
    public async Task ChangeUsernameAsync_is_a_no_op_when_the_new_username_is_identical()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var username = $"unchanged_{Guid.NewGuid():N}"[..20];
        var userId = await SeedUserAsync(db, username);
        var service = scope.ServiceProvider.GetRequiredService<IUsernameService>();

        var result = await service.ChangeUsernameAsync(userId, username);

        Assert.True(result.IsSuccess);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.Equal(1, await verifyDb.UsernameHistories.CountAsync(h => h.UserId == userId)); // no redundant row
    }

    [Fact]
    public async Task ChangeUsernameAsync_rejects_a_name_already_held_by_another_active_user_case_insensitively()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var takenUsername = $"Taken{suffix}";
        await SeedUserAsync(db, takenUsername);
        var userId = await SeedUserAsync(db, $"original_{suffix}");
        var service = scope.ServiceProvider.GetRequiredService<IUsernameService>();

        var result = await service.ChangeUsernameAsync(userId, takenUsername.ToUpperInvariant());

        Assert.True(result.IsFailure);
        Assert.Equal("username_taken", result.Error.Code);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.Equal(1, await verifyDb.UsernameHistories.CountAsync(h => h.UserId == userId)); // untouched
    }

    [Fact]
    public async Task ChangeUsernameAsync_allows_a_pure_case_change_and_still_opens_a_new_history_row()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var original = $"casechange{suffix}";
        var userId = await SeedUserAsync(db, original);
        var service = scope.ServiceProvider.GetRequiredService<IUsernameService>();

        var result = await service.ChangeUsernameAsync(userId, original.ToUpperInvariant());

        Assert.True(result.IsSuccess);
        Assert.Equal(original.ToUpperInvariant(), result.Value.Username);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.Equal(2, await verifyDb.UsernameHistories.CountAsync(h => h.UserId == userId));
    }

    [Fact]
    public async Task ChangeUsernameAsync_allows_reusing_a_name_freed_by_a_retired_user_per_BR_298()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var freedUsername = $"Freed{suffix}";
        var retiredUserId = await SeedUserAsync(db, freedUsername);
        var retiredUser = await db.Users.SingleAsync(u => u.UserId == retiredUserId);
        retiredUser.Status = UserStatus.Retired;
        retiredUser.RetiredAt = _clock.UtcNow;
        await db.SaveChangesAsync();
        var userId = await SeedUserAsync(db, $"original_{suffix}");
        var service = scope.ServiceProvider.GetRequiredService<IUsernameService>();

        var result = await service.ChangeUsernameAsync(userId, freedUsername);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }
}
