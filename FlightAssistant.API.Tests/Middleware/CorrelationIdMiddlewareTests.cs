using FlightAssistant.API.Middleware;
using Microsoft.AspNetCore.Http;

namespace FlightAssistant.API.Tests.Middleware;

/// <summary>
/// <see cref="CorrelationIdMiddleware"/> (#319). Only the <c>HttpContext.Items</c> exposure is
/// asserted here: <c>HttpResponse.OnStarting</c> callbacks are fired by the real hosting pipeline
/// (Kestrel/TestServer) on the first write, a mechanism a bare <see cref="DefaultHttpContext"/>
/// never triggers on its own — so the "the response header carries the identical value" half of the
/// contract is proven instead by the genuine HTTP round trips in
/// <c>AdminDataEndpointTests</c> (the reveal endpoint) and <c>AdminAuditEndpointTests</c> (a 400),
/// both of which run through <c>FlightAssistantWebApplicationFactory</c>'s real TestServer pipeline,
/// where <c>OnStarting</c> genuinely fires and the response header is genuinely observable.
/// </summary>
public class CorrelationIdMiddlewareTests
{
    private const string Header = "X-Correlation-Id";

    private static async Task<HttpContext> InvokeAsync(string? requestHeaderValue)
    {
        var ctx = new DefaultHttpContext();
        if (requestHeaderValue is not null)
            ctx.Request.Headers[Header] = requestHeaderValue;

        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task NoRequestHeader_MintsACorrelationId_AndStoresItInItems()
    {
        var ctx = await InvokeAsync(requestHeaderValue: null);

        var stored = Assert.IsType<string>(ctx.Items[CorrelationIdMiddleware.ItemsKey]);
        Assert.False(string.IsNullOrWhiteSpace(stored));
        Assert.Equal(32, stored.Length); // Guid.ToString("N")
    }

    [Fact]
    public async Task ValidClientSuppliedHeader_IsStoredInItemsUnchanged()
    {
        const string supplied = "client-supplied-correlation-id-123";

        var ctx = await InvokeAsync(supplied);

        Assert.Equal(supplied, ctx.Items[CorrelationIdMiddleware.ItemsKey]);
    }

    [Theory]
    [InlineData("has a space")]
    [InlineData("has/slash")]
    [InlineData("")]
    public async Task InvalidClientSuppliedHeader_IsRejected_AndANewOneIsMintedInstead(string invalid)
    {
        var ctx = await InvokeAsync(invalid);

        var stored = Assert.IsType<string>(ctx.Items[CorrelationIdMiddleware.ItemsKey]);
        Assert.NotEqual(invalid, stored);
        Assert.Equal(32, stored.Length);
    }

    [Fact]
    public async Task ClientSuppliedHeaderLongerThan64Chars_IsRejected_AndANewOneIsMintedInstead()
    {
        var tooLong = new string('a', 65);

        var ctx = await InvokeAsync(tooLong);

        var stored = Assert.IsType<string>(ctx.Items[CorrelationIdMiddleware.ItemsKey]);
        Assert.NotEqual(tooLong, stored);
    }

    /// <summary>
    /// Regression guard for ordering: <c>Items</c> must be populated *before* <c>next()</c> runs —
    /// that's what lets a downstream component (in the real pipeline, the <c>/admin</c> endpoint
    /// filter) observe it during the same request.
    /// </summary>
    [Fact]
    public async Task NextDelegate_ObservesTheCorrelationId_AlreadyPresentInItems()
    {
        string? seenDuringNext = null;
        var ctx = new DefaultHttpContext();
        var middleware = new CorrelationIdMiddleware(c =>
        {
            seenDuringNext = c.Items[CorrelationIdMiddleware.ItemsKey] as string;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx);

        Assert.False(string.IsNullOrWhiteSpace(seenDuringNext));
        Assert.Equal(ctx.Items[CorrelationIdMiddleware.ItemsKey], seenDuringNext);
    }
}
