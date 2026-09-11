using EplFantasy.Infrastructure.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure.Idempotency;

public class IdempotentAttributeTests
{
    private static InMemoryIdempotencyStore CreateStore() => new(new MemoryCache(new MemoryCacheOptions()));

    private static ActionExecutingContext CreateContext(IIdempotencyStore store, string? idempotencyKeyHeader, string path = "/api/v1/test")
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.Path = path;
        if (idempotencyKeyHeader is not null)
        {
            httpContext.Request.Headers[IdempotentAttribute.HeaderName] = idempotencyKeyHeader;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
    }

    private static ActionExecutionDelegate Producing(ActionContext actionContext, IActionResult result, Action onInvoked) => () =>
    {
        onInvoked();
        var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object()) { Result = result };
        return Task.FromResult(executed);
    };

    [Fact]
    public async Task Without_an_Idempotency_Key_header_the_action_always_executes()
    {
        var context = CreateContext(CreateStore(), idempotencyKeyHeader: null);
        var attribute = new IdempotentAttribute();
        var invoked = false;

        await attribute.OnActionExecutionAsync(context, Producing(context, new OkObjectResult(new { ok = true }), () => invoked = true));

        Assert.True(invoked);
    }

    [Fact]
    public async Task A_repeated_key_replays_the_first_response_without_re_executing_the_action()
    {
        var store = CreateStore();
        var attribute = new IdempotentAttribute();
        var callCount = 0;

        var first = CreateContext(store, idempotencyKeyHeader: "same-key");
        await attribute.OnActionExecutionAsync(first, Producing(first, new ObjectResult(new { value = "first" }) { StatusCode = 201 }, () => callCount++));

        var second = CreateContext(store, idempotencyKeyHeader: "same-key");
        await attribute.OnActionExecutionAsync(second, Producing(second, new ObjectResult(new { value = "second" }) { StatusCode = 201 }, () => callCount++));

        Assert.Equal(1, callCount);
        var replayed = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(201, replayed.StatusCode);
    }

    [Fact]
    public async Task Different_request_paths_with_the_same_key_do_not_collide()
    {
        var store = CreateStore();
        var attribute = new IdempotentAttribute();
        var callCount = 0;

        var first = CreateContext(store, idempotencyKeyHeader: "shared-key", path: "/api/v1/drafts/1/picks");
        await attribute.OnActionExecutionAsync(first, Producing(first, new ObjectResult(new { }) { StatusCode = 200 }, () => callCount++));

        var second = CreateContext(store, idempotencyKeyHeader: "shared-key", path: "/api/v1/rosters/1/submit");
        await attribute.OnActionExecutionAsync(second, Producing(second, new ObjectResult(new { }) { StatusCode = 200 }, () => callCount++));

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task A_non_2xx_result_is_never_cached_so_a_retry_executes_again()
    {
        var store = CreateStore();
        var attribute = new IdempotentAttribute();
        var callCount = 0;

        var first = CreateContext(store, idempotencyKeyHeader: "retry-key");
        await attribute.OnActionExecutionAsync(first, Producing(first, new ObjectResult(new { error = "bad" }) { StatusCode = 400 }, () => callCount++));

        var second = CreateContext(store, idempotencyKeyHeader: "retry-key");
        await attribute.OnActionExecutionAsync(second, Producing(second, new ObjectResult(new { error = "bad" }) { StatusCode = 400 }, () => callCount++));

        Assert.Equal(2, callCount);
    }
}
