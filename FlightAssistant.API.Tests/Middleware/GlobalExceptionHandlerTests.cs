using System.Text.Json;
using FlightAssistant.API.Middleware;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightAssistant.API.Tests.Middleware;

/// <summary>
/// Pins the two guarantees the problem-response contract makes: every error carries a
/// <c>traceId</c> for log correlation, and the operator-facing cause is surfaced outside
/// Production only.
/// </summary>
public class GlobalExceptionHandlerTests
{
    private static (GlobalExceptionHandler Handler, DefaultHttpContext Context) Sut(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);

        var ctx = new DefaultHttpContext { TraceIdentifier = "trace-abc" };
        ctx.Request.Path = "/flights/search";
        ctx.Response.Body = new MemoryStream();

        return (new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance, env), ctx);
    }

    private static async Task<JsonElement> BodyOf(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body);
        var json = await reader.ReadToEndAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static DomainException PlanRestriction() => new(
        "Flight data is temporarily unavailable.",
        500,
        "AeroAPI returned HTTP 403: {\"reason\":\"forbidden\",\"detail\":\"This API key does not have access to this endpoint.\"}");

    [Fact]
    public async Task DomainException_OutsideProduction_SurfacesDiagnosticDetail()
    {
        var (handler, ctx) = Sut("Development");

        var handled = await handler.TryHandleAsync(ctx, PlanRestriction(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(500, ctx.Response.StatusCode);

        var body = await BodyOf(ctx);
        Assert.Contains("forbidden", body.GetProperty("detail").GetString());
        Assert.Equal("/flights/search", body.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task DomainException_InProduction_OmitsDiagnosticDetail()
    {
        var (handler, ctx) = Sut("Production");

        await handler.TryHandleAsync(ctx, PlanRestriction(), CancellationToken.None);

        var body = await BodyOf(ctx);
        Assert.False(
            body.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null,
            "Production responses must not carry the upstream cause.");

        // The title is still the useful, non-leaking summary.
        Assert.Contains("temporarily unavailable", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Staging_IsTreatedAsNonProduction()
    {
        var (handler, ctx) = Sut("Staging");

        await handler.TryHandleAsync(ctx, PlanRestriction(), CancellationToken.None);

        var body = await BodyOf(ctx);
        Assert.Contains("forbidden", body.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task EveryResponse_CarriesATraceId(string environmentName)
    {
        var (handler, ctx) = Sut(environmentName);

        await handler.TryHandleAsync(ctx, PlanRestriction(), CancellationToken.None);

        var body = await BodyOf(ctx);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    /// <summary>
    /// #319: <c>traceId</c> must prefer <c>CorrelationIdMiddleware</c>'s value — stashed on
    /// <c>HttpContext.Items</c> under <see cref="CorrelationIdMiddleware.ItemsKey"/> — over both
    /// <see cref="Activity.Current"/> and <see cref="HttpContext.TraceIdentifier"/>. <c>Sut</c> sets
    /// <c>TraceIdentifier</c> to a fixed, *different* string ("trace-abc"), so asserting equality to
    /// the Items value (and inequality to "trace-abc") actually discriminates which source won,
    /// rather than merely checking "some non-empty string came back" the way the older tests in
    /// this file do.
    /// </summary>
    [Fact]
    public async Task TraceId_PrefersHttpContextItems_OverTraceIdentifier()
    {
        var (handler, ctx) = Sut("Development");
        ctx.Items[CorrelationIdMiddleware.ItemsKey] = "items-correlation-id";

        await handler.TryHandleAsync(ctx, PlanRestriction(), CancellationToken.None);

        var body = await BodyOf(ctx);
        Assert.Equal("items-correlation-id", body.GetProperty("traceId").GetString());
    }

    /// <summary>
    /// No <c>CorrelationIdMiddleware</c> value at all (defensive path — it is unconditional on
    /// every real request, so this only covers a caller that bypasses it) still falls back to
    /// <see cref="HttpContext.TraceIdentifier"/> when no <see cref="Activity"/> is ambient either,
    /// exactly as before #319.
    /// </summary>
    [Fact]
    public async Task TraceId_WithNoItemsValue_FallsBackToTraceIdentifier()
    {
        var (handler, ctx) = Sut("Development");

        await handler.TryHandleAsync(ctx, PlanRestriction(), CancellationToken.None);

        var body = await BodyOf(ctx);
        Assert.Equal("trace-abc", body.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task UnhandledException_CarriesTraceId_AndNoDetail()
    {
        var (handler, ctx) = Sut("Development");

        await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.Equal(500, ctx.Response.StatusCode);
        var body = await BodyOf(ctx);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        // The raw exception message is never echoed back, in any environment.
        Assert.DoesNotContain("boom", body.ToString());
    }

    [Fact]
    public async Task ValidationException_StillSerializesItsErrorsDictionary()
    {
        // The handler writes through a shared helper typed as ProblemDetails; this guards against
        // the errors payload being lost to the base type at serialization time.
        var (handler, ctx) = Sut("Development");
        var ex = new FluentValidation.ValidationException(
        [
            new FluentValidation.Results.ValidationFailure("DepartureIata", "must be 3 letters")
        ]);

        await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        var body = await BodyOf(ctx);
        var errors = body.GetProperty("errors");
        Assert.Equal("must be 3 letters", errors.GetProperty("DepartureIata")[0].GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task ThrottlingResponse_SetsRetryAfterHeader()
    {
        var (handler, ctx) = Sut("Production");
        var ex = new DomainException("Flight search is busy. Please try again shortly.", 503, "AeroAPI returned HTTP 429: {\"reason\":\"rate limit exceeded\"}");

        await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Equal("60", ctx.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task EveryProblemResponse_IsProblemJson()
    {
        // The spec declares application/problem+json on 122 responses; before #415 this handler
        // sent application/json, so handler-produced problems contradicted it while the
        // framework's own matched. All four arms funnel through one helper.
        var (handler, ctx) = Sut(Environments.Development);
        await handler.TryHandleAsync(ctx, PlanRestriction(), default);

        Assert.StartsWith("application/problem+json", ctx.Response.ContentType);
    }

    [Fact]
    public async Task ValidationArm_IsProblemJson()
    {
        var (handler, ctx) = Sut(Environments.Development);
        var ex = new FluentValidation.ValidationException(
        [
            new FluentValidation.Results.ValidationFailure("DepartureIata", "must be 3 letters")
        ]);

        await handler.TryHandleAsync(ctx, ex, default);

        Assert.StartsWith("application/problem+json", ctx.Response.ContentType);
    }

    [Fact]
    public async Task UnhandledArm_IsProblemJson()
    {
        var (handler, ctx) = Sut(Environments.Development);

        await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), default);

        Assert.StartsWith("application/problem+json", ctx.Response.ContentType);
    }
}
