using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace EplFantasy.Infrastructure.Idempotency;

/// <summary>
/// Architecture §9.3/OpenAPI conventions: opts a controller action into <c>Idempotency-Key</c>
/// handling (BR-237) — scoped to draft-pick and roster-submission endpoints specifically once those
/// feature tasks exist, never applied blanket-wide. A retried request that repeats the same key
/// gets back the exact status code and value its first attempt produced instead of the action
/// executing (and potentially double-submitting) a second time. The header is optional — a caller
/// that omits it always executes normally, with no dedup applied.
/// </summary>
/// <remarks>
/// Only replays/caches a 2xx <see cref="ObjectResult"/> (covers <c>Ok(value)</c>, <c>Created(...)</c>,
/// <c>CreatedAtAction(...)</c> — every one of those is an <see cref="ObjectResult"/> subclass). An
/// action that legitimately returns something else on success (e.g. <see cref="NoContentResult"/>)
/// is not deduplicated by this attribute as written — extend it if a future feature task needs that.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var headerValue) || string.IsNullOrWhiteSpace(headerValue))
        {
            await next();
            return;
        }

        var store = context.HttpContext.RequestServices.GetRequiredService<IIdempotencyStore>();
        var key = $"{context.HttpContext.Request.Path}:{headerValue}";

        if (store.TryGet(key, out var cached))
        {
            context.Result = new ObjectResult(cached.Value) { StatusCode = cached.StatusCode };
            return;
        }

        var executedContext = await next();

        if (executedContext.Exception is null && executedContext.Result is ObjectResult { StatusCode: >= 200 and < 300 } objectResult)
        {
            store.Set(key, new IdempotentResponse(objectResult.StatusCode ?? StatusCodes.Status200OK, objectResult.Value));
        }
    }
}
