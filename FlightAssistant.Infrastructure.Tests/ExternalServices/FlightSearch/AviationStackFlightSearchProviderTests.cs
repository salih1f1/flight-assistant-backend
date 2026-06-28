using System.Net;
using System.Text;
using FlightAssistant.Infrastructure.ExternalServices.FlightSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightSearch;

/// <summary>
/// Drives <see cref="AviationStackFlightSearchProvider"/> through its real <c>SearchAsync</c>
/// with a stub <see cref="HttpMessageHandler"/> returning a canned AviationStack
/// <c>/v1/flights</c> payload, so the mapping path is exercised end-to-end.
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

        var results = await Provider(handler).SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

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

        await Provider(handler).SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        var query = handler.LastRequestUri!.Query;
        Assert.Contains("dep_iata=LHR", query);
        Assert.Contains("arr_iata=JFK", query);
        Assert.Contains("flight_date=2025-06-15", query);
        Assert.Contains("access_key=dummy", query);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoData()
    {
        var handler = new StubHttpMessageHandler("""{ "data": [] }""");

        var results = await Provider(handler).SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_OnNonSuccessStatus()
    {
        var handler = new StubHttpMessageHandler("""{ "error": "boom" }""", HttpStatusCode.TooManyRequests);

        var results = await Provider(handler).SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        Assert.Empty(results);
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

        var results = await Provider(handler).SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        var flight = Assert.Single(results);
        Assert.Equal(new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc), flight.ScheduledDepartureUtc);
        Assert.Equal(DateTimeKind.Utc, flight.ScheduledDepartureUtc.Kind);
    }
}
