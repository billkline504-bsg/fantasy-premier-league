using EplFantasy.Infrastructure.Idempotency;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure.Idempotency;

public class InMemoryIdempotencyStoreTests
{
    private static InMemoryIdempotencyStore CreateStore() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void TryGet_returns_false_for_a_key_that_was_never_set()
    {
        var store = CreateStore();

        Assert.False(store.TryGet("never-set", out _));
    }

    [Fact]
    public void Set_then_TryGet_round_trips_the_exact_response()
    {
        var store = CreateStore();
        var response = new IdempotentResponse(201, new { id = "abc123" });

        store.Set("some-key", response);
        var found = store.TryGet("some-key", out var retrieved);

        Assert.True(found);
        Assert.Equal(201, retrieved.StatusCode);
        Assert.Same(response.Value, retrieved.Value);
    }

    [Fact]
    public void Different_keys_do_not_collide()
    {
        var store = CreateStore();
        store.Set("key-a", new IdempotentResponse(200, "a"));
        store.Set("key-b", new IdempotentResponse(200, "b"));

        store.TryGet("key-a", out var a);
        store.TryGet("key-b", out var b);

        Assert.Equal("a", a.Value);
        Assert.Equal("b", b.Value);
    }
}
