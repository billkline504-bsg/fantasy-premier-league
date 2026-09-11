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
/// Proves IT-09's default-icon-selection orchestration against real Postgres: BR-006's successful
/// change, and BR-011's rejection of both an unknown and an inactive ProfileIconId.
/// </summary>
public class ProfileServiceTests : IAsyncLifetime
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

    private async Task<(Guid UserId, Guid OriginalIconId)> SeedUserWithProfileAsync(EplFantasyDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var originalIcon = await db.ProfileIcons.Where(i => i.IsActive).OrderBy(i => i.SortOrder).FirstAsync();
        var user = new User { UserId = Guid.NewGuid(), Username = $"user_{suffix}", Email = $"user_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        db.UserProfiles.Add(new UserProfile { UserId = user.UserId, DefaultIconId = originalIcon.ProfileIconId, CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
        await db.SaveChangesAsync();
        return (user.UserId, originalIcon.ProfileIconId);
    }

    [Fact]
    public async Task UpdateDefaultIconAsync_changes_the_icon_to_a_real_active_one()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var (userId, originalIconId) = await SeedUserWithProfileAsync(db);
        var newIcon = await db.ProfileIcons.Where(i => i.IsActive && i.ProfileIconId != originalIconId).FirstAsync();
        var service = scope.ServiceProvider.GetRequiredService<IProfileService>();

        var result = await service.UpdateDefaultIconAsync(userId, newIcon.ProfileIconId);

        Assert.True(result.IsSuccess);
        Assert.Equal(newIcon.ProfileIconId, result.Value.DefaultIconId);
        Assert.Equal(_clock.UtcNow, result.Value.UpdatedAt);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.UserProfiles.SingleAsync(p => p.UserId == userId);
        Assert.Equal(newIcon.ProfileIconId, persisted.DefaultIconId);
    }

    [Fact]
    public async Task UpdateDefaultIconAsync_fails_for_an_unknown_profileIconId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var (userId, originalIconId) = await SeedUserWithProfileAsync(db);
        var service = scope.ServiceProvider.GetRequiredService<IProfileService>();

        var result = await service.UpdateDefaultIconAsync(userId, Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("profile_icon_not_active", result.Error.Code);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.UserProfiles.SingleAsync(p => p.UserId == userId);
        Assert.Equal(originalIconId, persisted.DefaultIconId); // untouched
    }

    [Fact]
    public async Task UpdateDefaultIconAsync_throws_for_a_real_but_inactive_profileIconId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var (userId, originalIconId) = await SeedUserWithProfileAsync(db);
        var inactiveIcon = new ProfileIcon { ProfileIconId = Guid.NewGuid(), Name = "Retired", AssetIdentifier = "icons/profile/retired.svg", IsActive = false, SortOrder = 99 };
        db.ProfileIcons.Add(inactiveIcon);
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<IProfileService>();

        await Assert.ThrowsAsync<ProfileIconNotActiveException>(() => service.UpdateDefaultIconAsync(userId, inactiveIcon.ProfileIconId));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.UserProfiles.SingleAsync(p => p.UserId == userId);
        Assert.Equal(originalIconId, persisted.DefaultIconId); // untouched
    }
}
