using Microsoft.Extensions.Caching.Memory;

namespace EplFantasy.Infrastructure.Idempotency;

/// <summary>
/// Registered Singleton, backed by <see cref="IMemoryCache"/>. Scoped to this one API instance —
/// the modular monolith (ADR-001) runs as a single process, so this is sufficient for BR-237's
/// actual intent (surviving a client's own retry-after-timeout within the same short window, not
/// surviving a process restart or coordinating across a horizontally-scaled fleet). If a future
/// feature task needs cross-instance durability, swap this implementation behind
/// <see cref="IIdempotencyStore"/> — <see cref="IdempotentAttribute"/> itself would not need to
/// change.
/// </summary>
public sealed class InMemoryIdempotencyStore(IMemoryCache cache) : IIdempotencyStore
{
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(24);

    public bool TryGet(string key, out IdempotentResponse response)
    {
        if (cache.TryGetValue(key, out IdempotentResponse? cached) && cached is not null)
        {
            response = cached;
            return true;
        }

        response = null!;
        return false;
    }

    public void Set(string key, IdempotentResponse response) => cache.Set(key, response, EntryLifetime);
}
