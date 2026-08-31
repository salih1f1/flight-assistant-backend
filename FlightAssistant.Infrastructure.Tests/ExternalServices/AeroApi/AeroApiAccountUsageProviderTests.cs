using System.Net;
using System.Text;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
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

    private static AeroApiAccountUsageProvider Provider(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/aeroapi/") };
        return new AeroApiAccountUsageProvider(
            http,
            Options.Create(new AeroApiOptions { ApiKey = "dummy" }),
            NullLogger<AeroApiAccountUsageProvider>.Instance);
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
}
