using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.FlightSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightSearch;

/// <summary>
/// Drives <see cref="AviationStackFlightSearchProvider"/> through its real <c>SearchAsync</c>
/// with a stub <see cref="HttpMessageHandler"/> returning a canned AviationStack
/// <c>/v1/flights</c> payload, so the mapping and error-handling paths are exercised end-to-end.
/// </summary>
public class AviationStackFlightSearchProviderTests
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

    private static AviationStackFlightSearchProvider Provider(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();
        return new AviationStackFlightSearchProvider(http, config, NullLogger<AviationStackFlightSearchProvider>.Instance);
    }

    private static FlightSearchCriteria Route(string from, string to, DateOnly date) => new(from, to, date, null);

    [Fact]
    public async Task SearchAsync_MapsFlightFields()
    {
        const string body = """
        {
          "data": [
            {
              "flight_date": "2025-06-15",
              "flight_status": "scheduled",
              "departure": { "iata": "LHR", "scheduled": "2025-06-15T10:30:00+00:00" },
              "arrival":   { "iata": "JFK", "scheduled": "2025-06-15T13:45:00+00:00" },
              "airline":   { "iata": "BA" },
              "flight":    { "number": "117", "iata": "BA117" },
              "aircraft":  { "icao": "B772" }
            }
          ]
        }
        """;
        var handler = new StubHttpMessageHandler(body);

        var results = await Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15)));

        var flight = Assert.Single(results);
        Assert.Equal("BA117", flight.FlightNumber);
        Assert.Equal("BA", flight.AirlineIata);
        Assert.Equal("LHR", flight.DepartureIata);
        Assert.Equal("JFK", flight.ArrivalIata);
        Assert.Equal(new DateOnly(2025, 6, 15), flight.ServiceDate);
        Assert.Equal(new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc), flight.ScheduledDepartureUtc);
        Assert.Equal(new DateTime(2025, 6, 15, 13, 45, 0, DateTimeKind.Utc), flight.ScheduledArrivalUtc);
        Assert.Equal("B772", flight.EquipmentIcao);
    }

    [Fact]
    public async Task SearchAsync_SendsRouteAndDateQueryParams()
    {
        var handler = new StubHttpMessageHandler("""{ "data": [] }""");

        await Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15)));

        var query = handler.LastRequestUri!.Query;
        Assert.Contains("dep_iata=LHR", query);
        Assert.Contains("arr_iata=JFK", query);
        Assert.Contains("dep_scheduled_time_dep=2025-06-15", query);
        Assert.Contains("access_key=dummy", query);
    }

    [Fact]
    public async Task SearchAsync_FlightNumberOnly_SendsFlightIataWithoutRoute()
    {
        var handler = new StubHttpMessageHandler("""{ "data": [] }""");

        await Provider(handler).SearchAsync(new FlightSearchCriteria(null, null, new DateOnly(2025, 6, 15), "BA117"));

        var query = handler.LastRequestUri!.Query;
        Assert.Contains("flight_iata=BA117", query);
        Assert.DoesNotContain("dep_iata=", query);
        Assert.DoesNotContain("arr_iata=", query);
        Assert.Contains("dep_scheduled_time_dep=2025-06-15", query);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoData()
    {
        var handler = new StubHttpMessageHandler("""{ "data": [] }""");

        var results = await Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15)));

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_Throws502_OnNonSuccessStatusWithoutErrorBody()
    {
        var handler = new StubHttpMessageHandler("""{ "data": [] }""", HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15))));

        Assert.Equal(502, ex.StatusCode);
    }

    [Theory]
    [InlineData("rate_limit_reached")]
    [InlineData("usage_limit_reached")]
    public async Task SearchAsync_Throws503_OnThrottling(string code)
    {
        var handler = new StubHttpMessageHandler($$"""{ "error": { "code": "{{code}}", "message": "throttled" } }""", HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15))));

        Assert.Equal(503, ex.StatusCode);
    }

    [Theory]
    [InlineData("invalid_access_key")]
    [InlineData("missing_access_key")]
    [InlineData("function_access_restricted")]
    public async Task SearchAsync_Throws500_OnConfigErrors_WithoutLeakingCause(string code)
    {
        var handler = new StubHttpMessageHandler($$"""{ "error": { "code": "{{code}}", "message": "secret detail" } }""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15))));

        Assert.Equal(500, ex.StatusCode);
        Assert.DoesNotContain("secret detail", ex.Message);
        Assert.DoesNotContain(code, ex.Message);
    }

    [Fact]
    public async Task SearchAsync_Throws400_OnValidationError_SurfacingContext()
    {
        const string body = """
        {
          "error": {
            "code": "validation_error",
            "message": "Validation failed",
            "context": { "flight_date": [ { "key": "invalid_flight_date", "message": "flight_date is invalid" } ] }
          }
        }
        """;
        var handler = new StubHttpMessageHandler(body, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15))));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("flight_date is invalid", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_Throws502_OnUnknownErrorCode()
    {
        var handler = new StubHttpMessageHandler("""{ "error": { "code": "internal_error", "message": "boom" } }""", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15))));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_MapsErrorBody_EvenUnderHttp200()
    {
        // AviationStack sometimes returns an error object with an HTTP 200.
        var handler = new StubHttpMessageHandler("""{ "error": { "code": "usage_limit_reached", "message": "over quota" } }""", HttpStatusCode.OK);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15))));

        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_ParsesOffsetlessTimestampAsUtc()
    {
        const string body = """
        {
          "data": [
            {
              "departure": { "iata": "LHR", "scheduled": "2025-06-15T10:30:00" },
              "arrival":   { "iata": "JFK", "scheduled": "2025-06-15T13:45:00" },
              "airline":   { "iata": "BA" },
              "flight":    { "iata": "BA117" }
            }
          ]
        }
        """;
        var handler = new StubHttpMessageHandler(body);

        var results = await Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15)));

        var flight = Assert.Single(results);
        Assert.Equal(new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc), flight.ScheduledDepartureUtc);
        Assert.Equal(DateTimeKind.Utc, flight.ScheduledDepartureUtc.Kind);
    }
}
