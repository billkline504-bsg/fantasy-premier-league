using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// Boots the real EplFantasy.Api host (Program.cs, unmodified) against a disposable Testcontainers
/// Postgres, with a tightened "auth" rate limit (so its threshold is reachable in a handful of
/// requests within a test) and this test project's <see cref="DiagnosticsTestController"/> added as
/// an extra MVC application part, purely so IT-F14's middleware has something to exercise end to
/// end without a real feature-task controller existing yet.
/// </summary>
/// <remarks>
/// Configuration is injected via process environment variables, not <c>IWebHostBuilder.ConfigureAppConfiguration</c>.
/// Program.cs reads several settings *eagerly* off <c>builder.Configuration</c> before
/// <c>builder.Build()</c> runs (the connection string, JwtOptions, and — as of IT-F14 —
/// RateLimitOptions) — a legitimate pattern for a real deployed process, where environment
/// variables and appsettings files are already loaded before Main starts. But
/// <see cref="WebApplicationFactory{TEntryPoint}"/>'s <c>ConfigureAppConfiguration</c>/<c>ConfigureWebHost</c>
/// overrides for a minimal-hosting Program.cs are only layered into <c>builder.Configuration</c>'s
/// sources partway through the framework's own bootstrap of that builder — demonstrably *after*
/// Program.cs's own eager reads already ran (confirmed by probing: a live <c>IConfiguration</c>
/// resolved from DI at request time reflected the override correctly, while a POCO built from
/// <c>Get&lt;T&gt;()</c> at the same config key during Program.cs's top-level execution did not).
/// Environment variables sidestep this entirely — <c>WebApplicationBuilder.CreateBuilder</c> loads
/// them as one of its first steps, so they're visible to every subsequent read, including the eager
/// ones. <c>Environment.SetEnvironmentVariable</c> is process-wide, but this project only ever runs
/// one <see cref="EplFantasyApiFactory"/> at a time, so that's not a practical concern here.
/// </remarks>
public sealed class EplFantasyApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _container.GetConnectionString());
        // High enough that ordinary functional tests sharing one test class's fixture (several
        // AuthController calls each) never trip it — a dedicated test that deliberately exceeds it
        // (ApiConventionsTests/AuthRateLimitingTests) simply loops further, since it needs its own
        // isolated IClassFixture instance either way (see ApiTestCollection's remarks: exhausting a
        // shared instance's limit would otherwise fail every other test sharing it).
        Environment.SetEnvironmentVariable("RateLimiting__PermitLimit", "50");
        Environment.SetEnvironmentVariable("RateLimiting__WindowSeconds", "60");
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public override async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(EplFantasyApiFactory).Assembly);
        });
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
}
