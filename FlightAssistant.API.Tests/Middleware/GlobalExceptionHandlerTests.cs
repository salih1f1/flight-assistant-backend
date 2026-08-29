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
        "Flight search is unavailable due to a flight data provider configuration issue.",
        500,
        "AviationStack returned HTTP 403 function_access_restricted: Your current subscription plan does not support this API function.");

    [Fact]
    public async Task DomainException_OutsideProduction_SurfacesDiagnosticDetail()
    {
        var (handler, ctx) = Sut("Development");

        var handled = await handler.TryHandleAsync(ctx, PlanRestriction(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(500, ctx.Response.StatusCode);

        var body = await BodyOf(ctx);
        Assert.Contains("function_access_restricted", body.GetProperty("detail").GetString());
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
        Assert.Contains("configuration issue", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Staging_IsTreatedAsNonProduction()
    {
        var (handler, ctx) = Sut("Staging");

        await handler.TryHandleAsync(ctx, PlanRestriction(), CancellationToken.None);

        var body = await BodyOf(ctx);
        Assert.Contains("function_access_restricted", body.GetProperty("detail").GetString());
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
        var ex = new DomainException("Flight search is temporarily unavailable due to high demand.", 503, "AviationStack returned HTTP 429 rate_limit_reached: throttled");

        await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Equal("60", ctx.Response.Headers.RetryAfter);
    }
}
