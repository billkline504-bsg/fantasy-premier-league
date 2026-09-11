using EplFantasy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace EplFantasy.TestSupport;

/// <summary>
/// The one place every integration test builds an <see cref="EplFantasyDbContext"/>'s options
/// from a raw connection string (as opposed to going through <c>AddInfrastructure</c>'s DI
/// wiring) — centralized specifically so every test gets the enum-mapped
/// <see cref="NpgsqlDataSource"/> (skipping it makes every enum column reject writes with a
/// "column is of type X but expression is of type integer" error) and the same
/// <see cref="CoreEventId.ManyServiceProvidersCreatedWarning"/> suppression
/// <c>AddInfrastructure</c> itself applies — each integration test spins up its own disposable
/// Testcontainers Postgres with a distinct connection string by design, so EF Core's internal
/// "you've created suspiciously many service providers" heuristic is a false positive here, not a
/// bug to fix by sharing a data source across tests (which would defeat their isolation).
/// </summary>
public static class TestDbContextOptionsFactory
{
    public static DbContextOptions<EplFantasyDbContext> Create(string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.MapEplFantasyEnums();
        var dataSource = dataSourceBuilder.Build();

        return new DbContextOptionsBuilder<EplFantasyDbContext>()
            .UseNpgsql(dataSource)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
    }
}
