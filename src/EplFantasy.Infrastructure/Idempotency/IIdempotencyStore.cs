namespace EplFantasy.Infrastructure.Idempotency;

/// <summary>The cached outcome of one previously-executed idempotent request, replayed verbatim (status code and result value) for a retry carrying the same key.</summary>
public sealed record IdempotentResponse(int StatusCode, object? Value);

/// <summary>
/// Backing store for <see cref="IdempotentAttribute"/> (BR-237, Architecture §9.3). Keyed on the
/// caller's <c>Idempotency-Key</c> header value combined with the request path — see
/// <see cref="IdempotentAttribute"/> for why the path is folded into the key.
/// </summary>
public interface IIdempotencyStore
{
    bool TryGet(string key, out IdempotentResponse response);

    void Set(string key, IdempotentResponse response);
}
