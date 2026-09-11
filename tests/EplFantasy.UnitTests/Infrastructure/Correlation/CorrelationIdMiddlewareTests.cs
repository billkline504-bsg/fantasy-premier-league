using EplFantasy.Infrastructure.Correlation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure.Correlation;

public class CorrelationIdMiddlewareTests
{
    private static CorrelationIdMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, NullLogger<CorrelationIdMiddleware>.Instance);

    [Fact]
    public async Task Generates_a_correlation_id_when_the_caller_sent_none()
    {
        var context = new DefaultHttpContext();
        string? seenDuringPipeline = null;

        var middleware = CreateMiddleware(ctx =>
        {
            seenDuringPipeline = ctx.Items[CorrelationIdMiddleware.HttpContextItemKey] as string;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.False(string.IsNullOrWhiteSpace(seenDuringPipeline));
        Assert.True(Guid.TryParse(seenDuringPipeline, out _));
        Assert.Equal(seenDuringPipeline, context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task Reuses_the_callers_own_correlation_id_when_one_was_sent()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "caller-supplied-id-123";
        string? seenDuringPipeline = null;

        var middleware = CreateMiddleware(ctx =>
        {
            seenDuringPipeline = ctx.Items[CorrelationIdMiddleware.HttpContextItemKey] as string;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal("caller-supplied-id-123", seenDuringPipeline);
        Assert.Equal("caller-supplied-id-123", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task Calls_the_next_delegate_exactly_once()
    {
        var context = new DefaultHttpContext();
        var callCount = 0;

        var middleware = CreateMiddleware(_ =>
        {
            callCount++;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(1, callCount);
    }
}
