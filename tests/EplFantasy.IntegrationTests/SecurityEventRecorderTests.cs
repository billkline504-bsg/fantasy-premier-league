using EplFantasy.Administration;
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
/// Proves IT-F14's ISecurityEventRecorder actually persists a row against real Postgres — unlike
/// IAdministrativeActionRecorder, this commits itself (see the interface's own remarks on why),
/// so the real thing to prove is that the row survives, not just that it's staged.
/// </summary>
public class SecurityEventRecorderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
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

    [Fact]
    public async Task RecordAsync_persists_a_security_event_row_immediately()
    {
        await using var scope = _provider.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<ISecurityEventRecorder>();

        await recorder.RecordAsync(
            SecurityEventType.RateLimitBlocked,
            endpoint: "/api/v1/auth/login",
            scope: "IP 203.0.113.44",
            detail: "Rate limit exceeded for policy \"auth\" (10 requests per 60s).");

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var events = await db.SecurityEvents.Where(e => e.Endpoint == "/api/v1/auth/login").ToListAsync();

        var securityEvent = Assert.Single(events);
        Assert.Equal(SecurityEventType.RateLimitBlocked, securityEvent.EventType);
        Assert.Equal("IP 203.0.113.44", securityEvent.Scope);
        Assert.Contains("auth", securityEvent.Detail);
        Assert.Equal(_clock.UtcNow, securityEvent.OccurredAt);
    }

    [Fact]
    public async Task RecordAsync_does_not_require_a_caller_supplied_SaveChangesAsync()
    {
        // Deliberately no db.SaveChangesAsync() call anywhere in this test — proving RecordAsync's
        // own commit is what persists the row, unlike IAdministrativeActionRecorder.Record().
        await using var scope = _provider.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<ISecurityEventRecorder>();

        await recorder.RecordAsync(SecurityEventType.RateLimitBlocked, "/api/v1/auth/register", "IP 198.51.100.7", "blocked");

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.True(await db.SecurityEvents.AnyAsync(e => e.Endpoint == "/api/v1/auth/register"));
    }
}
