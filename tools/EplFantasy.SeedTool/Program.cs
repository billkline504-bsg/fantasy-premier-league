using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// One-off local/dev seeding tool: populates Club/Player/Gameweek/Fixture reference data by calling
// the real FPL API (ADR-009's FplApiDataSource, wired up exactly as AddInfrastructure already does
// for the API itself) — no OpenAPI-documented endpoint exists for this (PlayerDataController.cs's
// own remarks: "Actually *triggering* that sync ... is left for later"), so this reuses the app's
// existing IPlayerDataSyncService rather than duplicating its upsert logic in a script.
var connectionString = args.ElementAtOrDefault(0)
    ?? Environment.GetEnvironmentVariable("EPLFANTASY_CONNECTION_STRING")
    ?? "Host=localhost;Port=55432;Database=eplfantasy;Username=eplfantasy_app;Password=dev-only-not-a-real-secret";

var eplSeasonIdentifier = args.ElementAtOrDefault(1)
    ?? Environment.GetEnvironmentVariable("EPLFANTASY_SEASON_IDENTIFIER")
    ?? "2025-26";

var services = new ServiceCollection();
services.AddInfrastructure(connectionString);
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();

var syncService = scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>();

Console.WriteLine("Syncing clubs from the FPL API...");
await syncService.SyncClubsAsync();

Console.WriteLine("Syncing players from the FPL API...");
await syncService.SyncPlayersAsync();

Console.WriteLine($"Syncing gameweeks and fixtures for season '{eplSeasonIdentifier}'...");
await syncService.SyncGameweeksAndFixturesAsync(eplSeasonIdentifier);

var dbContext = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
var clubCount = await dbContext.Clubs.CountAsync();
var playerCount = await dbContext.Players.CountAsync();
var gameweekCount = await dbContext.Gameweeks.CountAsync();
var fixtureCount = await dbContext.Fixtures.CountAsync();

Console.WriteLine($"Done. clubs={clubCount} players={playerCount} gameweeks={gameweekCount} fixtures={fixtureCount}");
