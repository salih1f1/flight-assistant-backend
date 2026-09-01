using System.Net;
using System.Text;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Drives <see cref="AeroApiAccountUsageProvider"/> against the recorded AeroAPI
/// <c>/account/usage</c> payload (see
/// <c>FlightAssistant.Infrastructure.Tests/Fixtures/AeroApi/account-usage.json</c>).
///
/// <para>
/// The fixture's <c>resource_details</c> rows do not sum to its totals, and that is faithful to
/// the real response: the account's totals span operations the excerpt does not list. It is also
/// the reason the provider reads every total directly instead of deriving one by summing rows.
/// </para>
///
/// <para>
/// Error mapping is deliberately asserted, not reimplemented: this provider routes failures
/// through the same <c>AeroApiResponse.ReadOrThrowAsync</c> as flight search and status, and these
/// tests exist to prove it is actually wired to it rather than to prove that helper's own logic
/// (which <c>AeroApiResponseTests</c> covers).
/// </para>
/// </summary>
public class AeroApiAccountUsageProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 9, 30, 10, 30, 0, TimeSpan.Zero);

    private static string ReadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AeroApi", "account-usage.json"));

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public Uri? LastRequestUri { get; private set; }

        public StubHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Throws before any response is produced — the transport-failure path (unreachable
    /// host, DNS failure, connection reset), the only branch that does not go through
    /// <c>AeroApiResponse.ReadOrThrowAsync</c>.</summary>
    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection reset by peer");
    }

    /// <summary>Records every log call made through it, so a test can assert a warning was actually
    /// emitted rather than an unreadable total silently reporting as zero with no trace.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static AeroApiAccountUsageProvider Provider(
        HttpMessageHandler handler, ILogger<AeroApiAccountUsageProvider>? logger = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/aeroapi/") };
        return new AeroApiAccountUsageProvider(
            http,
            Options.Create(new AeroApiOptions { ApiKey = "dummy" }),
            logger ?? NullLogger<AeroApiAccountUsageProvider>.Instance);
    }

    [Fact]
    public async Task GetUsageAsync_ParsesTheRecordedResponse_Totals()
    {
        var provider = Provider(new StubHttpMessageHandler(ReadFixture()));

        var usage = await provider.GetUsageAsync(Start, End);

        Assert.Equal(42, usage.TotalCalls);
        Assert.Equal(46, usage.TotalPages);
        Assert.Equal(0.66m, usage.TotalCostUsd);
        Assert.Equal(0.0m, usage.TotalDiscountCostUsd);
        Assert.Equal(41, usage.TotalSuccessfulCalls);
        Assert.Equal(1, usage.TotalFailedCalls);
    }

    [Fact]
    public async Task GetUsageAsync_ParsesEveryResourceDetailRow_WithItsOwnPagesAndCost()
    {
        var provider = Provider(new StubHttpMessageHandler(ReadFixture()));

        var usage = await provider.GetUsageAsync(Start, End);

        Assert.Equal(4, usage.ByOperation.Count);

        var schedules = Assert.Single(
            usage.ByOperation, o => o.Operation == "GET /schedules/{date_start}/{date_end}");
        Assert.Equal(21, schedules.Calls);
        Assert.Equal(21, schedules.Pages);
        Assert.Equal(0.42m, schedules.CostUsd);
        Assert.Equal(21, schedules.SuccessfulCalls);
        Assert.Equal(0, schedules.FailedCalls);

        // Pages exceed calls here — the whole reason cost cannot be derived from a call count.
        var airports = Assert.Single(usage.ByOperation, o => o.Operation == "GET /airports/{id}");
        Assert.Equal(4, airports.Calls);
        Assert.Equal(8, airports.Pages);
        Assert.Equal(0.04m, airports.CostUsd);

        // A failed call that was still billed — it must appear in Calls and CostUsd, not be netted out.
        var search = Assert.Single(usage.ByOperation, o => o.Operation == "GET /flights/search");
        Assert.Equal(1, search.Calls);
        Assert.Equal(1, search.FailedCalls);
        Assert.Equal(0, search.SuccessfulCalls);
        Assert.Equal(0.05m, search.CostUsd);
    }

    [Fact]
    public async Task GetUsageAsync_ReportsItsOwnEndpointAsFree()
    {
        var provider = Provider(new StubHttpMessageHandler(ReadFixture()));

        var usage = await provider.GetUsageAsync(Start, End);

        // Documents why polling this endpoint is safe on a $5/month tier.
        var self = Assert.Single(usage.ByOperation, o => o.Operation == "GET /account/usage");
        Assert.Equal(0m, self.CostUsd);
    }

    [Fact]
    public async Task GetUsageAsync_SendsTheRangeAsUtcInstants_OnTheUsageEndpoint()
    {
        var handler = new StubHttpMessageHandler(ReadFixture());
        var provider = Provider(handler);

        // A non-UTC offset must be normalised, not sent verbatim: 08:00+02:00 is 06:00Z.
        await provider.GetUsageAsync(
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));

        var uri = Assert.IsType<Uri>(handler.LastRequestUri);
        Assert.Equal("/aeroapi/account/usage", uri.AbsolutePath);
        Assert.Contains("start=2026-09-01T06%3A00%3A00Z", uri.Query);
        Assert.Contains("end=2026-09-02T00%3A00%3A00Z", uri.Query);
    }

    [Fact]
    public async Task GetUsageAsync_ReturnsEmptyBreakdown_WhenTheResponseHasNoResourceDetails()
    {
        var provider = Provider(new StubHttpMessageHandler(
            """{"total_calls":0,"total_pages":0,"total_cost":0.0}"""));

        var usage = await provider.GetUsageAsync(Start, End);

        Assert.Empty(usage.ByOperation);
        Assert.Equal(0, usage.TotalCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 500)]
    [InlineData(HttpStatusCode.Forbidden, 500)]
    [InlineData(HttpStatusCode.TooManyRequests, 503)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    [InlineData(HttpStatusCode.NotFound, 502)]
    public async Task GetUsageAsync_MapsUpstreamFailures_TheSameWayEveryOtherAeroApiCallerDoes(
        HttpStatusCode upstream, int expectedStatus)
    {
        var provider = Provider(new StubHttpMessageHandler(
            """{"title":"unavailable","detail":"upstream said no"}""", upstream));

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.GetUsageAsync(Start, End));

        Assert.Equal(expectedStatus, ex.StatusCode);
    }

    [Fact]
    public async Task GetUsageAsync_Maps200WithAnUnparseableBody_To502()
    {
        var provider = Provider(new StubHttpMessageHandler("not json at all"));

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.GetUsageAsync(Start, End));

        Assert.Equal(502, ex.StatusCode);
    }

    /// <summary>
    /// The transport-failure catch (unreachable host, DNS failure, connection reset) is the one
    /// branch that does not go through <c>AeroApiResponse.ReadOrThrowAsync</c> — it never gets a
    /// response to hand it. Not equivalent to <c>AeroApiResponse.ReadOrThrowAsync</c>'s own tests:
    /// this is the only path in this provider that maps its own <see cref="Exception"/> to 502.
    /// <c>TaskCanceledException</c> from an <c>HttpClient</c> timeout is deliberately not covered
    /// here — it derives from <c>OperationCanceledException</c>, is excluded by this catch's own
    /// filter, and escapes as an unhandled 500. That matches <c>AeroApiFlightStatusProvider</c>'s
    /// identical catch, so it is a house pattern here, not a regression.
    /// </summary>
    [Fact]
    public async Task GetUsageAsync_Maps502_WhenTheHttpRequestThrowsBeforeAnyResponse()
    {
        var provider = Provider(new ThrowingHttpMessageHandler());

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.GetUsageAsync(Start, End));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task GetUsageAsync_NeverLeaksTheApiKey_IntoTheDiagnosticDetail()
    {
        var http = new HttpClient(new StubHttpMessageHandler(
            """{"error":"bad key","x-apikey":"super-secret-key"}""", HttpStatusCode.Unauthorized))
        { BaseAddress = new Uri("http://stub/aeroapi/") };
        var provider = new AeroApiAccountUsageProvider(
            http,
            Options.Create(new AeroApiOptions { ApiKey = "super-secret-key" }),
            NullLogger<AeroApiAccountUsageProvider>.Instance);

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.GetUsageAsync(Start, End));

        Assert.DoesNotContain("super-secret-key", ex.DiagnosticDetail);
        Assert.DoesNotContain("super-secret-key", ex.Message);
    }

    // ── The quiet-wrong-answer guard ─────────────────────────────────────────

    [Fact]
    public async Task GetUsageAsync_LogsAWarning_WhenTotalCallsIsEntirelyMissing()
    {
        var logger = new CapturingLogger<AeroApiAccountUsageProvider>();
        var provider = Provider(
            new StubHttpMessageHandler("""{"total_pages":0,"total_cost":0.0}"""), logger);

        await provider.GetUsageAsync(Start, End);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("total_calls"));
    }

    /// <summary>
    /// The case the guard exists to catch: a total that is present but not a number reads as zero
    /// exactly as silently as a missing one, unless it is named in the log too. Without this, a
    /// response shaped like this one would report $0.00 spend on a metered account with no warning
    /// at all — the field the guard used to check, total_calls, is a valid number here.
    /// </summary>
    [Fact]
    public async Task GetUsageAsync_LogsAWarning_WhenTotalCostIsPresentButNotANumber()
    {
        var logger = new CapturingLogger<AeroApiAccountUsageProvider>();
        var provider = Provider(
            new StubHttpMessageHandler("""{"total_calls":42,"total_cost":"0.66"}"""), logger);

        var usage = await provider.GetUsageAsync(Start, End);

        Assert.Equal(0m, usage.TotalCostUsd); // reads as zero — the log is the only trace
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("total_cost"));
    }

    [Fact]
    public async Task GetUsageAsync_DoesNotWarn_WhenEveryTotalIsAReadableNumber()
    {
        var logger = new CapturingLogger<AeroApiAccountUsageProvider>();
        var provider = Provider(new StubHttpMessageHandler(ReadFixture()), logger);

        await provider.GetUsageAsync(Start, End);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
