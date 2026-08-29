using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
using FlightAssistant.Infrastructure.ExternalServices.FlightSearch;
using FlightAssistant.Application.Interfaces.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightSearch;

/// <summary>
/// Drives <see cref="AviationStackLiveSearchProvider"/> through its real <c>SearchAsync</c>
/// with a stub <see cref="HttpMessageHandler"/> returning a canned AviationStack
/// <c>/v1/flights</c> payload, so the mapping and error-handling paths are exercised end-to-end.
/// </summary>
public class AviationStackLiveSearchProviderTests
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

    private static AviationStackLiveSearchProvider Provider(
        StubHttpMessageHandler handler, IRefDataRepository? refData = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();
        return new AviationStackLiveSearchProvider(
            http, refData ?? NoRefData(), TimezoneLookup(), config,
            NullLogger<AviationStackLiveSearchProvider>.Instance);
    }

    /// <summary>
    /// A provider timezone lookup wired to a stub that returns nothing, so tests exercise the
    /// reference-data path only. Pass a real body to exercise the provider fallback.
    /// </summary>
    private static AviationStackAirportTimezoneLookup TimezoneLookup(string body = """{ "data": [] }""")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();
        return new AviationStackAirportTimezoneLookup(
            new HttpClient(new StubHttpMessageHandler(body)) { BaseAddress = new Uri("http://stub/") },
            config, NullLogger<AviationStackAirportTimezoneLookup>.Instance);
    }


    /// <summary>
    /// Reference data that knows no airport, so tests rely on the timezone the payload itself
    /// carries — which is what the real API supplies on this endpoint.
    /// </summary>
    private static IRefDataRepository NoRefData()
    {
        var repo = Substitute.For<IRefDataRepository>();
        repo.GetAirportTimezoneByIataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        return repo;
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
              "departure": { "iata": "LHR", "timezone": "Europe/London", "scheduled": "2025-06-15T10:30:00+00:00" },
              "arrival":   { "iata": "JFK", "timezone": "America/New_York", "scheduled": "2025-06-15T13:45:00+00:00" },
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
        Assert.Equal(["BA117"], flight.FlightNumbers);
        Assert.Equal("BA117", flight.OperatingFlightNumber);
        Assert.Equal("BA", flight.AirlineIata);
        Assert.Equal("LHR", flight.DepartureIata);
        Assert.Equal("JFK", flight.ArrivalIata);
        Assert.Equal(new DateOnly(2025, 6, 15), flight.ServiceDate);
        // "+00:00" in the payload is decoration: 10:30 is London local (BST, UTC+1) and 13:45 is
        // New York local (EDT, UTC-4), giving a 7h15m flight rather than an impossible 3h15m.
        Assert.Equal(new DateTime(2025, 6, 15, 9, 30, 0, DateTimeKind.Utc), flight.ScheduledDepartureUtc);
        Assert.Equal(new DateTime(2025, 6, 15, 17, 45, 0, DateTimeKind.Utc), flight.ScheduledArrivalUtc);
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
    public async Task SearchAsync_TreatsOffsetlessTimestampAsAirportLocal()
    {
        const string body = """
        {
          "data": [
            {
              "departure": { "iata": "LHR", "timezone": "Europe/London", "scheduled": "2025-06-15T10:30:00" },
              "arrival":   { "iata": "JFK", "timezone": "America/New_York", "scheduled": "2025-06-15T13:45:00" },
              "airline":   { "iata": "BA" },
              "flight":    { "iata": "BA117" }
            }
          ]
        }
        """;
        var handler = new StubHttpMessageHandler(body);

        var results = await Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15)));

        var flight = Assert.Single(results);
        Assert.Equal(new DateTime(2025, 6, 15, 9, 30, 0, DateTimeKind.Utc), flight.ScheduledDepartureUtc);
        Assert.Equal(DateTimeKind.Utc, flight.ScheduledDepartureUtc.Kind);
    }

    /// <summary>
    /// Mirrors a real SOF→IST response: three physical departures, each sold under several
    /// numbers, with `codeshared` naming the operating flight on every marketing row.
    /// </summary>
    private const string CodeshareBody = """
    {
      "data": [
        { "departure": { "iata": "SOF", "timezone": "Europe/Sofia", "scheduled": "2026-08-29T06:35:00+00:00" },
          "arrival":   { "iata": "IST", "timezone": "Europe/Istanbul", "scheduled": "2026-08-29T08:05:00+00:00" },
          "airline": { "iata": "TK" },
          "flight": { "iata": "TK1028", "codeshared": null },
          "aircraft": { "icao": "A21N" } },
        { "departure": { "iata": "SOF", "timezone": "Europe/Sofia", "scheduled": "2026-08-29T06:35:00+00:00" },
          "arrival":   { "iata": "IST", "timezone": "Europe/Istanbul", "scheduled": "2026-08-29T08:05:00+00:00" },
          "airline": { "iata": "6E" },
          "flight": { "iata": "6E4120", "codeshared": { "flight_iata": "tk1028" } } },
        { "departure": { "iata": "SOF", "timezone": "Europe/Sofia", "scheduled": "2026-08-29T06:35:00+00:00" },
          "arrival":   { "iata": "IST", "timezone": "Europe/Istanbul", "scheduled": "2026-08-29T08:05:00+00:00" },
          "airline": { "iata": "MS" },
          "flight": { "iata": "MS9668", "codeshared": { "flight_iata": "tk1028" } } },
        { "departure": { "iata": "SOF", "timezone": "Europe/Sofia", "scheduled": "2026-08-29T12:50:00+00:00" },
          "arrival":   { "iata": "IST", "timezone": "Europe/Istanbul", "scheduled": "2026-08-29T14:20:00+00:00" },
          "airline": { "iata": "TK" },
          "flight": { "iata": "TK1032", "codeshared": null } },
        { "departure": { "iata": "SOF", "timezone": "Europe/Sofia", "scheduled": "2026-08-29T12:50:00+00:00" },
          "arrival":   { "iata": "IST", "timezone": "Europe/Istanbul", "scheduled": "2026-08-29T14:20:00+00:00" },
          "airline": { "iata": "6E" },
          "flight": { "iata": "6E4118", "codeshared": { "flight_iata": "tk1032" } } }
      ]
    }
    """;

    [Fact]
    public async Task SearchAsync_CollapsesCodesharesOntoTheOperatingFlight()
    {
        var handler = new StubHttpMessageHandler(CodeshareBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 8, 29)));

        // Five provider rows are two physical departures.
        Assert.Equal(2, results.Count);

        var first = results[0];
        Assert.Equal("TK1028", first.OperatingFlightNumber);
        Assert.Equal("TK", first.AirlineIata);
        Assert.Equal(["6E4120", "MS9668", "TK1028"], first.FlightNumbers);
        // The aircraft is only on the operating row, but belongs to the whole group.
        Assert.Equal("A21N", first.EquipmentIcao);

        Assert.Equal("TK1032", results[1].OperatingFlightNumber);
        Assert.Equal(["6E4118", "TK1032"], results[1].FlightNumbers);
    }

    [Fact]
    public async Task SearchAsync_OrdersGroupsByDepartureTime()
    {
        var handler = new StubHttpMessageHandler(CodeshareBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 8, 29)));

        Assert.True(results[0].ScheduledDepartureUtc < results[1].ScheduledDepartureUtc);
    }

    [Fact]
    public async Task SearchAsync_FlightNumberSearchForACodeshare_StillReturnsIt()
    {
        // Searching "6E4120" returns only the marketing row — the operating row is absent, so the
        // group must still be built from the `codeshared` pointer rather than dropped.
        const string body = """
        {
          "data": [
            { "departure": { "iata": "SOF", "timezone": "Europe/Sofia", "scheduled": "2026-08-29T06:35:00+00:00" },
              "arrival":   { "iata": "IST", "timezone": "Europe/Istanbul", "scheduled": "2026-08-29T08:05:00+00:00" },
              "airline": { "iata": "6E" },
              "flight": { "iata": "6E4120", "codeshared": { "flight_iata": "tk1028" } } }
          ]
        }
        """;
        var handler = new StubHttpMessageHandler(body);

        var results = await Provider(handler).SearchAsync(
            new FlightSearchCriteria(null, null, new DateOnly(2026, 8, 29), "6E4120"));

        var flight = Assert.Single(results);
        Assert.Contains("6E4120", flight.FlightNumbers);
        Assert.Equal("TK1028", flight.OperatingFlightNumber);
    }

    [Fact]
    public async Task SearchAsync_DoesNotMergeDistinctFlightsSharingATimetableSlot()
    {
        // Two genuinely separate aircraft leaving at the same minute on the same route: neither
        // is a codeshare of the other, so they must stay apart.
        const string body = """
        {
          "data": [
            { "departure": { "iata": "SOF", "timezone": "Europe/Sofia", "scheduled": "2026-08-29T06:35:00+00:00" },
              "arrival":   { "iata": "IST", "timezone": "Europe/Istanbul", "scheduled": "2026-08-29T08:05:00+00:00" },
              "airline": { "iata": "TK" }, "flight": { "iata": "TK1028", "codeshared": null } },
            { "departure": { "iata": "SOF", "timezone": "Europe/Sofia", "scheduled": "2026-08-29T06:35:00+00:00" },
              "arrival":   { "iata": "IST", "timezone": "Europe/Istanbul", "scheduled": "2026-08-29T08:05:00+00:00" },
              "airline": { "iata": "PC" }, "flight": { "iata": "PC1234", "codeshared": null } }
          ]
        }
        """;
        var handler = new StubHttpMessageHandler(body);

        var results = await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 8, 29)));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_CarriesUpstreamCauseInDiagnosticDetail()
    {
        // The exact failure that motivated this: a plan restriction returned as HTTP 403.
        var handler = new StubHttpMessageHandler(
            """{ "error": { "code": "function_access_restricted", "message": "Your current subscription plan does not support this API function." } }""",
            HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10))));

        Assert.Equal(500, ex.StatusCode);
        Assert.NotNull(ex.DiagnosticDetail);
        Assert.Contains("403", ex.DiagnosticDetail);
        Assert.Contains("function_access_restricted", ex.DiagnosticDetail);
        Assert.Contains("does not support this API function", ex.DiagnosticDetail);
    }

    [Theory]
    [InlineData("rate_limit_reached", HttpStatusCode.TooManyRequests)]
    [InlineData("invalid_access_key", HttpStatusCode.Unauthorized)]
    [InlineData("validation_error", HttpStatusCode.BadRequest)]
    [InlineData("internal_error", HttpStatusCode.InternalServerError)]
    public async Task SearchAsync_DiagnosticDetail_NeverLeaksTheApiKey(string code, HttpStatusCode status)
    {
        // DiagnosticDetail is rendered into the response body outside Production, so it must never
        // pick up the request URL, which carries access_key.
        var handler = new StubHttpMessageHandler($$"""{ "error": { "code": "{{code}}", "message": "boom" } }""", status);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15))));

        Assert.NotNull(ex.DiagnosticDetail);
        // "access_key=" is the query-parameter form: its presence would mean the request URL
        // leaked in. The bare token also appears inside the legitimate error code
        // "invalid_access_key", so matching on it alone would be a false positive.
        Assert.DoesNotContain("access_key=", ex.DiagnosticDetail);
        Assert.DoesNotContain("dummy", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task SearchAsync_UnparseableBody_Throws502_WithDiagnosticDetail()
    {
        var handler = new StubHttpMessageHandler("<html>gateway timeout</html>", HttpStatusCode.GatewayTimeout);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Provider(handler).SearchAsync(Route("LHR", "JFK", new DateOnly(2025, 6, 15))));

        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("504", ex.DiagnosticDetail);
    }
}
