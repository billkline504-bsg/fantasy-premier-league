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
/// Proves IT-57's UsernameHistoryResolver (F-013.2, BR-014/BR-175/BR-272/BR-326) against real
/// Postgres: a historical instant always resolves the username that was active back then, not the
/// User's current one, even across an intervening rename; the same resolution keeps working
/// correctly after the User retires (BR-014/BR-175/BR-272 — retirement never breaks or hides
/// history); and the batched <c>ResolveManyAsOfAsync</c> resolves each request's own instant
/// independently even when several requests share the same User (e.g. one User's FantasyTeams
/// across two different Seasons, each predating a different rename).
/// </summary>
public class UsernameHistoryResolverTests : IAsyncLifetime
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
    public async Task ResolveAsOfAsync_returns_the_username_active_at_that_instant_even_after_a_later_rename()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = await SeedUserAsync(db, $"season1_{suffix}");
        var recordInstant = _clock.UtcNow; // e.g. when a Season-1 FantasyTeam was created.

        _clock.AdvanceBy(TimeSpan.FromDays(365));
        await scope.ServiceProvider.GetRequiredService<IUsernameService>().ChangeUsernameAsync(userId, $"season2_{suffix}");

        var resolver = scope.ServiceProvider.GetRequiredService<IUsernameHistoryResolver>();
        var resolvedAtRecordTime = await resolver.ResolveAsOfAsync(userId, recordInstant);
        var resolvedNow = await resolver.ResolveAsOfAsync(userId, _clock.UtcNow);

        Assert.Equal($"season1_{suffix}", resolvedAtRecordTime); // BR-326: the Season-1-era record keeps its Season-1-era name.
        Assert.Equal($"season2_{suffix}", resolvedNow); // a request as-of "now" still gets the current name.
    }

    [Fact]
    public async Task ResolveAsOfAsync_keeps_resolving_correctly_after_the_User_retires()
    {
        // The task's own required scenario (BR-014/BR-175/BR-272): a retired User's earlier
        // history must never break or disappear.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = await SeedUserAsync(db, $"season1_{suffix}");
        var recordInstant = _clock.UtcNow;

        _clock.AdvanceBy(TimeSpan.FromDays(365));
        await scope.ServiceProvider.GetRequiredService<IUsernameService>().ChangeUsernameAsync(userId, $"renamed_{suffix}");
        await scope.ServiceProvider.GetRequiredService<IUserRetirementService>().RetireAsync(userId);

        var resolver = scope.ServiceProvider.GetRequiredService<IUsernameHistoryResolver>();
        var resolved = await resolver.ResolveAsOfAsync(userId, recordInstant);

        Assert.Equal($"season1_{suffix}", resolved); // still resolves — retirement neither breaks nor changes it.
    }

    [Fact]
    public async Task ResolveManyAsOfAsync_resolves_each_requests_own_instant_independently_for_the_same_User()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = await SeedUserAsync(db, $"season1_{suffix}");
        var season1Instant = _clock.UtcNow;

        _clock.AdvanceBy(TimeSpan.FromDays(365));
        await scope.ServiceProvider.GetRequiredService<IUsernameService>().ChangeUsernameAsync(userId, $"season2_{suffix}");
        var season2Instant = _clock.UtcNow;

        var season1TeamId = Guid.NewGuid();
        var season2TeamId = Guid.NewGuid();
        var resolver = scope.ServiceProvider.GetRequiredService<IUsernameHistoryResolver>();
        var resolved = await resolver.ResolveManyAsOfAsync(
            [(season1TeamId, userId, season1Instant), (season2TeamId, userId, season2Instant)]);

        Assert.Equal($"season1_{suffix}", resolved[season1TeamId]);
        Assert.Equal($"season2_{suffix}", resolved[season2TeamId]);
    }
}
