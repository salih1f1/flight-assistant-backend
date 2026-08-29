using System.Net;
using System.Text;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.FlightStatus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightStatus;

/// <summary>
/// The status provider used to answer every upstream failure with <c>null</c>, which callers read
/// as "no status available" — a provider outage was indistinguishable from a flight with no
/// updates yet. These tests pin the distinction: real failures throw, genuinely empty results
/// still return <c>null</c>.
/// </summary>
public class AviationStackFlightStatusProviderErrorTests
{
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static AviationStackFlightStatusProvider Provider(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();
        return new AviationStackFlightStatusProvider(
            http, config, NullLogger<AviationStackFlightStatusProvider>.Instance);
    }

    [Fact]
    public async Task GetStatusAsync_Throws500_OnPlanRestriction_InsteadOfReturningNull()
    {
        var handler = new StubHttpMessageHandler(
            """{ "error": { "code": "function_access_restricted", "message": "Your current subscription plan does not support this API function." } }""",
            HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).GetStatusAsync("BA123", new DateOnly(2025, 6, 15)));

        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("function_access_restricted", ex.DiagnosticDetail);
        Assert.DoesNotContain("function_access_restricted", ex.Message);
    }

    [Fact]
    public async Task GetStatusAsync_Throws503_OnThrottling()
    {
        var handler = new StubHttpMessageHandler(
            """{ "error": { "code": "rate_limit_reached", "message": "throttled" } }""",
            HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).GetStatusAsync("BA123", new DateOnly(2025, 6, 15)));

        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_Throws502_OnNonSuccessStatusWithoutErrorBody()
    {
        var handler = new StubHttpMessageHandler("""{ "data": [] }""", HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).GetStatusAsync("BA123", new DateOnly(2025, 6, 15)));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_OnSuccessfulEmptyData()
    {
        // No status published yet is not a failure — this must stay null so
        // SyncFlightStatusCommand keeps reporting "not updated" rather than erroring.
        var handler = new StubHttpMessageHandler("""{ "data": [] }""");

        var result = await Provider(handler).GetStatusAsync("BA123", new DateOnly(2025, 6, 15));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatusAsync_EscapesQueryParameters()
    {
        var handler = new StubHttpMessageHandler("""{ "data": [] }""");

        await Provider(handler).GetStatusAsync("BA 123&x", new DateOnly(2025, 6, 15));

        Assert.NotNull(handler.LastRequestUri);
        var query = handler.LastRequestUri!.Query;
        Assert.Contains("flight_iata=BA%20123%26x", query);
    }
}
