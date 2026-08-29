using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
using FlightAssistant.Infrastructure.ExternalServices.FlightSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightSearch;

/// <summary>
/// Drives <see cref="AviationStackScheduleSearchProvider"/> against canned
/// <c>/v1/flightsFuture</c> payloads shaped exactly like the live API's: lowercase values,
/// airport-local times with no offset, and no codeshare marker.
/// </summary>
public class AviationStackScheduleSearchProviderTests
{
    private sealed class StubHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Three SOF→IST departures on 2026-10-10, mirroring the real payload.</summary>
    private const string SofBody = """
    {
      "pagination": { "limit": 100, "offset": 0, "count": 5, "total": 5 },
      "data": [
        { "weekday": "6",
          "departure": { "iataCode": "sof", "scheduledTime": "2026-10-10 09:35:00" },
          "arrival":   { "iataCode": "ist", "scheduledTime": "2026-10-10 11:05:00" },
          "aircraft": { "modelCode": "a321" },
          "airline": { "iataCode": "tk" }, "flight": { "iataNumber": "tk1028" } },
        { "weekday": "6",
          "departure": { "iataCode": "sof", "scheduledTime": "2026-10-10 09:35:00" },
          "arrival":   { "iataCode": "ist", "scheduledTime": "2026-10-10 11:05:00" },
          "aircraft": { "modelCode": "a321" },
          "airline": { "iataCode": "6e" }, "flight": { "iataNumber": "6e4120" } },
        { "weekday": "6",
          "departure": { "iataCode": "sof", "scheduledTime": "2026-10-10 09:35:00" },
          "arrival":   { "iataCode": "ist", "scheduledTime": "2026-10-10 11:05:00" },
          "aircraft": { "modelCode": "a321" },
          "airline": { "iataCode": "ms" }, "flight": { "iataNumber": "ms9668" } },
        { "weekday": "6",
          "departure": { "iataCode": "sof", "scheduledTime": "2026-10-10 21:10:00" },
          "arrival":   { "iataCode": "ist", "scheduledTime": "2026-10-10 22:40:00" },
          "airline": { "iataCode": "tk" }, "flight": { "iataNumber": "tk1030" } },
        { "weekday": "6",
          "departure": { "iataCode": "sof", "scheduledTime": "2026-10-10 12:00:00" },
          "arrival":   { "iataCode": "cdg", "scheduledTime": "2026-10-10 14:30:00" },
          "airline": { "iataCode": "af" }, "flight": { "iataNumber": "af1789" } }
      ]
    }
    """;

    private static AviationStackScheduleSearchProvider Provider(
        StubHttpMessageHandler handler,
        AviationStackRouteResolver? resolver = null,
        AviationStackAirportTimezoneLookup? timezones = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();

        resolver ??= new AviationStackRouteResolver(
            new HttpClient(new StubHttpMessageHandler("""{ "data": [] }""")) { BaseAddress = new Uri("http://stub/") },
            config,
            NullLogger<AviationStackRouteResolver>.Instance);

        return new AviationStackScheduleSearchProvider(
            http, resolver, timezones ?? AllZones(), config,
            NullLogger<AviationStackScheduleSearchProvider>.Instance);
    }

    /// <summary>
    /// Answers /v1/airports per requested IATA code, so each airport can resolve differently — a
    /// null entry stands for "the provider knows no timezone for this one".
    /// </summary>
    private sealed class TimezoneStub(Dictionary<string, string?> zones) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var iata = query["iata_code"] ?? "";
            var body = zones.TryGetValue(iata, out var tz) && tz is not null
                ? $$"""{ "data": [ { "iata_code": "{{iata}}", "timezone": "{{tz}}" } ] }"""
                : """{ "data": [] }""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static AviationStackAirportTimezoneLookup TimezoneLookup(TimezoneStub stub)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<string?>(null));
        return new AviationStackAirportTimezoneLookup(
            new HttpClient(stub) { BaseAddress = new Uri("http://stub/") },
            cache, config, NullLogger<AviationStackAirportTimezoneLookup>.Instance);
    }

    private static AviationStackAirportTimezoneLookup ZonesFor(params (string Iata, string? Tz)[] zones) =>
        TimezoneLookup(new TimezoneStub(zones.ToDictionary(z => z.Iata, z => z.Tz)));

    /// <summary>The real zones for every airport these fixtures mention.</summary>
    private static AviationStackAirportTimezoneLookup AllZones() =>
        ZonesFor(("SOF", "Europe/Sofia"), ("IST", "Europe/Istanbul"), ("CDG", "Europe/Paris"));

    private static FlightSearchCriteria Route(string from, string to, DateOnly date) => new(from, to, date, null);

    [Fact]
    public async Task SearchAsync_ConvertsAirportLocalTimesToUtc()
    {
        // Sofia is UTC+3 on 2026-10-10 (EEST; DST ends 25 October), Istanbul is UTC+3 year-round.
        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10)));

        var first = results[0];
        Assert.Equal(new DateTime(2026, 10, 10, 6, 35, 0, DateTimeKind.Utc), first.ScheduledDepartureUtc);
        Assert.Equal(new DateTime(2026, 10, 10, 8, 5, 0, DateTimeKind.Utc), first.ScheduledArrivalUtc);
    }

    [Fact]
    public async Task SearchAsync_ConvertsEachEndUsingItsOwnTimezone()
    {
        // SOF (UTC+3) → CDG (UTC+2 in October): departure and arrival use different zones.
        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "CDG", new DateOnly(2026, 10, 10)));

        var flight = Assert.Single(results);
        Assert.Equal(new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc), flight.ScheduledDepartureUtc);
        Assert.Equal(new DateTime(2026, 10, 10, 12, 30, 0, DateTimeKind.Utc), flight.ScheduledArrivalUtc);
    }

    [Fact]
    public async Task SearchAsync_AppliesTheOffsetInEffectOnThatDate_NotToday()
    {
        // Same wall-clock time, one side of the DST switch each. Sofia is UTC+2 in January and
        // UTC+3 in July, so a fixed offset would get one of these wrong.
        const string winter = """
        { "data": [ { "departure": { "iataCode": "sof", "scheduledTime": "2027-01-15 09:35:00" },
                      "arrival":   { "iataCode": "ist", "scheduledTime": "2027-01-15 11:05:00" },
                      "airline": { "iataCode": "tk" }, "flight": { "iataNumber": "tk1028" } } ] }
        """;
        var results = await Provider(new StubHttpMessageHandler(winter))
            .SearchAsync(Route("SOF", "IST", new DateOnly(2027, 1, 15)));

        // 09:35 EET (UTC+2) → 07:35Z, versus 06:35Z for the same clock time in October.
        Assert.Equal(new DateTime(2027, 1, 15, 7, 35, 0, DateTimeKind.Utc), results[0].ScheduledDepartureUtc);
    }

    [Fact]
    public async Task SearchAsync_KeepsFlightsFallingInADstGap()
    {
        // Europe/Sofia jumps 03:00 → 04:00 on 2027-03-28, so 03:30 never happens locally. A naive
        // conversion throws; the flight should survive rather than vanish once a year.
        const string gap = """
        { "data": [ { "departure": { "iataCode": "sof", "scheduledTime": "2027-03-28 03:30:00" },
                      "arrival":   { "iataCode": "ist", "scheduledTime": "2027-03-28 05:00:00" },
                      "airline": { "iataCode": "tk" }, "flight": { "iataNumber": "tk1028" } } ] }
        """;
        var results = await Provider(new StubHttpMessageHandler(gap))
            .SearchAsync(Route("SOF", "IST", new DateOnly(2027, 3, 28)));

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAsync_GroupsCodesharesByRouteAndTimes_WithoutClaimingAnOperator()
    {
        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10)));

        Assert.Equal(2, results.Count);
        Assert.Equal(["6E4120", "MS9668", "TK1028"], results[0].FlightNumbers);
        // The schedules endpoint gives no codeshare marker, so this must not be guessed.
        Assert.Null(results[0].OperatingFlightNumber);
        Assert.Null(results[0].AirlineIata);
        Assert.Equal(["TK1030"], results[1].FlightNumbers);
    }

    [Fact]
    public async Task SearchAsync_FiltersByArrivalAirportClientSide()
    {
        // The provider ignores arr_iata, so the CDG flight must be filtered out here.
        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10)));

        Assert.All(results, r => Assert.Equal("IST", r.ArrivalIata));
    }

    [Fact]
    public async Task SearchAsync_UppercasesProviderValues()
    {
        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10)));

        Assert.Equal("SOF", results[0].DepartureIata);
        Assert.Equal("IST", results[0].ArrivalIata);
        Assert.Equal("A321", results[0].EquipmentIcao);
        Assert.All(results[0].FlightNumbers, n => Assert.Equal(n.ToUpperInvariant(), n));
    }

    [Fact]
    public async Task SearchAsync_SendsRequiredQueryParams()
    {
        var handler = new StubHttpMessageHandler(SofBody);

        await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10)));

        var query = handler.Requests[0].Query;
        Assert.Contains("iataCode=SOF", query);
        Assert.Contains("type=departure", query);
        Assert.Contains("date=2026-10-10", query);
    }

    [Fact]
    public async Task SearchAsync_ResolvesZonesFromTheProvider_NotLocalReferenceData()
    {
        // The schedules payload carries no timezone, so every zone here came from /v1/airports.
        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10)));

        Assert.Equal(2, results.Count);
        Assert.Equal(new DateTime(2026, 10, 10, 6, 35, 0, DateTimeKind.Utc), results[0].ScheduledDepartureUtc);
    }

    [Fact]
    public async Task SearchAsync_LooksUpEachAirportOnce_HoweverManyRowsMentionIt()
    {
        // Five rows over three airports must not cost five lookups on a rate-limited plan.
        var stub = new TimezoneStub(new Dictionary<string, string?>
        {
            ["SOF"] = "Europe/Sofia", ["IST"] = "Europe/Istanbul", ["CDG"] = "Europe/Paris"
        });

        await Provider(new StubHttpMessageHandler(SofBody), timezones: TimezoneLookup(stub))
            .SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10)));

        Assert.Equal(2, stub.Calls); // SOF and IST only; the CDG row is filtered out before conversion
    }

    [Fact]
    public async Task SearchAsync_Throws_WhenEveryMatchIsDroppedForMissingTimezones()
    {
        // Silently returning [] here is indistinguishable from "this route has no flights", which
        // is precisely how the original bug hid. It must be loud, and name the airport.
        var handler = new StubHttpMessageHandler(SofBody);
        var lookup = ZonesFor(("SOF", "Europe/Sofia"), ("IST", null));

        var ex = await Assert.ThrowsAsync<FlightAssistant.Domain.Common.Exceptions.DomainException>(
            () => Provider(handler, timezones: lookup).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10))));

        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("IST", ex.DiagnosticDetail);
        Assert.DoesNotContain("IST", ex.Message); // the client-facing text stays generic
    }

    [Theory]
    [InlineData("2")]            // a numeric GMT offset, not an IANA id
    [InlineData("+02:00")]
    [InlineData("Not/AZone")]
    public async Task SearchAsync_Throws_WhenTheZoneIsPresentButUnusable(string unusable)
    {
        // A present-but-invalid value must never be accepted as authoritative — it would place
        // every flight at the wrong instant while looking correct.
        var handler = new StubHttpMessageHandler(SofBody);
        var lookup = ZonesFor(("SOF", "Europe/Sofia"), ("IST", unusable));

        var ex = await Assert.ThrowsAsync<FlightAssistant.Domain.Common.Exceptions.DomainException>(
            () => Provider(handler, timezones: lookup).SearchAsync(Route("SOF", "IST", new DateOnly(2026, 10, 10))));

        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenTheRouteGenuinelyHasNoFlights()
    {
        // No matches at all is a legitimate empty result, and must NOT be confused with the
        // timezone failure above.
        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler).SearchAsync(Route("SOF", "LHR", new DateOnly(2026, 10, 10)));

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_FlightNumberOnly_ResolvesDepartureAirportFirst()
    {
        const string routesBody = """
        { "data": [ { "departure": { "iata": "SOF", "timezone": "Europe/Sofia" },
                      "arrival": { "iata": "IST" }, "flight": { "number": "1028" } } ] }
        """;
        var routesHandler = new StubHttpMessageHandler(routesBody);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();
        var resolver = new AviationStackRouteResolver(
            new HttpClient(routesHandler) { BaseAddress = new Uri("http://stub/") },
            config, NullLogger<AviationStackRouteResolver>.Instance);

        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler, resolver: resolver).SearchAsync(
            new FlightSearchCriteria(null, null, new DateOnly(2026, 10, 10), "TK1028"));

        Assert.Contains("airline_iata=TK", routesHandler.Requests[0].Query);
        Assert.Contains("flight_number=1028", routesHandler.Requests[0].Query);
        Assert.Contains("iataCode=SOF", handler.Requests[0].Query);

        var flight = Assert.Single(results);
        Assert.Contains("TK1028", flight.FlightNumbers);
    }

    [Fact]
    public async Task SearchAsync_FlightNumberOnly_ReturnsEmpty_WhenRouteUnresolvable()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();
        var resolver = new AviationStackRouteResolver(
            new HttpClient(new StubHttpMessageHandler("""{ "data": [] }""")) { BaseAddress = new Uri("http://stub/") },
            config, NullLogger<AviationStackRouteResolver>.Instance);

        var handler = new StubHttpMessageHandler(SofBody);

        var results = await Provider(handler, resolver: resolver).SearchAsync(
            new FlightSearchCriteria(null, null, new DateOnly(2026, 10, 10), "ZZ9999"));

        Assert.Empty(results);
        Assert.Empty(handler.Requests); // never reached the schedules endpoint
    }

    [Fact]
    public async Task SearchAsync_SurfacesStringShapedErrorBody()
    {
        // The schedules endpoint answers an out-of-range date this way, unlike the object-shaped
        // errors the rest of the API returns.
        var handler = new StubHttpMessageHandler(
            """{"error":"date must be before 2027-08-15"}""", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<FlightAssistant.Domain.Common.Exceptions.DomainException>(
            () => Provider(handler).SearchAsync(Route("SOF", "IST", new DateOnly(2027, 9, 1))));

        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("date must be before 2027-08-15", ex.DiagnosticDetail);
    }
}
