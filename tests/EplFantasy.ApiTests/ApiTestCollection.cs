using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// Every test class that uses <see cref="EplFantasyApiFactory"/> must carry <c>[Collection(nameof(ApiTestCollection))]</c>.
/// xUnit runs different test classes' collections in parallel by default, but
/// <see cref="EplFantasyApiFactory.InitializeAsync"/> injects its connection string and rate-limit
/// override via *process-wide* environment variables (see that method's own remarks on why —
/// Program.cs's eager config reads leave no other option). Two factories booting concurrently in
/// separate collections could race and have one host boot against the other's connection string.
/// Putting them all in one named collection makes xUnit run them strictly sequentially — each
/// factory still gets its own instance and its own independent rate-limiter state (IClassFixture
/// semantics are unaffected by collection grouping; only parallelism is), but never overlapping
/// with another factory's environment-variable window.
/// </summary>
[CollectionDefinition(nameof(ApiTestCollection), DisableParallelization = true)]
public class ApiTestCollection;
