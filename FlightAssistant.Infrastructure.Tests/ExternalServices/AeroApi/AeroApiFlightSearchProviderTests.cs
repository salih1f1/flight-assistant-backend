using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Drives <see cref="AeroApiFlightSearchProvider"/> against recorded AeroAPI <c>/schedules</c>
/// payloads (see <c>FlightAssistant.Infrastructure.Tests/Fixtures/AeroApi</c>) and, for the
/// local-day window shift, against synthetic <see cref="TimeZoneInfo"/> instances that force the
/// DST edge cases real-world zones are deliberately built to avoid at midnight.
/// </summary>
public class AeroApiFlightSearchProviderTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AeroApi");

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixturesDir, name));

    /// <summary>Serves a fixed sequence of responses, repeating the last one once exhausted (used
    /// to simulate an endpoint that keeps paging forever, to prove the <c>MaxPages</c> cap bites).</summary>
    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly List<(HttpStatusCode Status, string Body)> _responses;

        public ScriptedHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses) =>
            _responses = responses.ToList();

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var index = Math.Min(Requests.Count - 1, _responses.Count - 1);
            var (status, body) = _responses[index];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Answers <c>/airports/{iata}</c> with a fixed timezone per code; an absent entry
    /// means "the provider knows no timezone for this one" (mirrors the AviationStack test helper).</summary>
    private sealed class TimezoneStub(Dictionary<string, string?> zones) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var iata = request.RequestUri!.Segments[^1].ToUpperInvariant();
            var body = zones.TryGetValue(iata, out var tz) && tz is not null
                ? $$"""{ "code_iata": "{{iata}}", "timezone": "{{tz}}" }"""
                : """{ "title": "not found" }""";
            var status = zones.TryGetValue(iata, out var known) && known is not null
                ? HttpStatusCode.OK
                : HttpStatusCode.NotFound;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static AeroApiAirportTimezoneLookup TimezoneLookup(params (string Iata, string? Tz)[] zones)
    {
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var http = new HttpClient(new TimezoneStub(zones.ToDictionary(z => z.Iata, z => z.Tz)))
        {
            BaseAddress = new Uri("http://stub/aeroapi/")
        };

        return new AeroApiAirportTimezoneLookup(
            http, cache, Options.Create(new AeroApiOptions { ApiKey = "dummy" }),
            NullLogger<AeroApiAirportTimezoneLookup>.Instance);
    }

    private static AeroApiFlightSearchProvider Provider(
        ScriptedHttpMessageHandler handler,
        AeroApiAirportTimezoneLookup? timezones = null,
        int maxPages = 4)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/aeroapi/") };
        return new AeroApiFlightSearchProvider(
            http,
            timezones ?? TimezoneLookup(),
            Options.Create(new AeroApiOptions { ApiKey = "dummy", MaxPages = maxPages }),
            NullLogger<AeroApiFlightSearchProvider>.Instance);
    }

    // ------------------------------------------------------------------
    // Mapping: codeshares, malformed rows
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_MapsEachCodeshareRow_WithTheOperatingFlightNumberFromActualIdentIata()
    {
        var handler = new ScriptedHttpMessageHandler(
            (HttpStatusCode.OK, ReadFixture("schedules-codeshares-on.json")));
        var timezones = TimezoneLookup(("SOF", "Europe/Sofia"));

        var results = await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria("SOF", null, new DateOnly(2026, 9, 29), "TK1028"));

        Assert.Equal(5, results.Count);

        var byIdent = results.ToDictionary(r => r.FlightNumbers.Single());
        Assert.Equal("TK1028", byIdent["RJ3710"].OperatingFlightNumber);
        Assert.Equal("TK1028", byIdent["MS9668"].OperatingFlightNumber);
        Assert.Equal("TK1028", byIdent["6E4120"].OperatingFlightNumber);
        Assert.Equal("TK1028", byIdent["SQ6228"].OperatingFlightNumber);
        // actual_ident_iata is null on the operating row itself — falls back to its own ident.
        Assert.Equal("TK1028", byIdent["TK1028"].OperatingFlightNumber);

        // AirlineIata is the first two characters of THIS row's own ident_iata, per the interim
        // one-row-per-departure mapping — not the operating carrier's code.
        Assert.Equal("RJ", byIdent["RJ3710"].AirlineIata);
        Assert.Equal("TK", byIdent["TK1028"].AirlineIata);

        Assert.All(results, r =>
        {
            Assert.Equal("SOF", r.DepartureIata);
            Assert.Equal("IST", r.ArrivalIata);
            Assert.Equal("A321", r.EquipmentIcao);
        });
    }

    [Fact]
    public async Task SearchAsync_IncludeCodesharesTrue_ReachesTheUrl()
    {
        var handler = new ScriptedHttpMessageHandler(
            (HttpStatusCode.OK, ReadFixture("schedules-codeshares-on.json")));
        var timezones = TimezoneLookup(("SOF", "Europe/Sofia"));

        await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria("SOF", "IST", new DateOnly(2026, 9, 29), null));

        Assert.Single(handler.Requests);
        Assert.Contains("include_codeshares=true", handler.Requests[0].Query);
    }

    [Fact]
    public async Task SearchAsync_PushesAirlineAndFlightNumber_ForAFlightNumberSearch()
    {
        var handler = new ScriptedHttpMessageHandler(
            (HttpStatusCode.OK, ReadFixture("schedules-codeshares-on.json")));
        var timezones = TimezoneLookup(("SOF", "Europe/Sofia"));

        await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria("SOF", null, new DateOnly(2026, 9, 29), "TK1028"));

        var query = handler.Requests[0].Query;
        Assert.Contains("airline=TK", query);
        Assert.Contains("flight_number=1028", query);
        Assert.Contains("origin=SOF", query);
    }

    [Fact]
    public async Task SearchAsync_SkipsMalformedRows_RatherThanThrowing()
    {
        const string body = """
        {
          "scheduled": [
            { "ident_iata": "BA100", "actual_ident_iata": null, "origin_iata": "LHR", "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T10:00:00Z", "scheduled_in": "2026-09-29T18:00:00Z", "aircraft_type": "B77W" },
            { "actual_ident_iata": null, "origin_iata": "LHR", "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T11:00:00Z", "scheduled_in": "2026-09-29T19:00:00Z" },
            { "ident_iata": "BA102", "actual_ident_iata": null, "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T12:00:00Z", "scheduled_in": "2026-09-29T20:00:00Z" },
            { "ident_iata": "BA103", "actual_ident_iata": null, "origin_iata": "LHR", "destination_iata": "JFK",
              "scheduled_in": "2026-09-29T21:00:00Z" },
            { "ident_iata": "BA104", "actual_ident_iata": null, "origin_iata": "LHR", "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T13:00:00Z" },
            { "ident_iata": "BA105", "actual_ident_iata": null, "origin_iata": "LHR", "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T14:00:00Z", "scheduled_in": "2026-09-29T22:00:00Z", "aircraft_type": "A359" }
          ],
          "links": null,
          "num_pages": 1
        }
        """;
        var handler = new ScriptedHttpMessageHandler((HttpStatusCode.OK, body));
        var timezones = TimezoneLookup(("LHR", "Europe/London"));

        var results = await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria("LHR", "JFK", new DateOnly(2026, 9, 29), null));

        var idents = results.Select(r => r.FlightNumbers.Single()).OrderBy(i => i).ToList();
        Assert.Equal(["BA100", "BA105"], idents);
    }

    // ------------------------------------------------------------------
    // The local-day window shift (AKL -> SYD)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_ShiftsTheWindowToTheOriginsLocalDay_ForAkl()
    {
        // AKL (Pacific/Auckland) is UTC+13 on 2026-09-29 (already in NZDT). The correct window for
        // local 2026-09-29 is therefore the exact UTC instants of AKL midnight -> next AKL
        // midnight: 2026-09-28T11:00:00Z (inclusive) -> 2026-09-29T11:00:00Z (exclusive). Verified
        // independently against the real system tzdata via TimeZoneInfo below, not hardcoded from
        // the design doc's illustrative (and slightly different) UTC+12 arithmetic.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var expectedStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, 29, 0, 0, 0), zone);
        var expectedEnd = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, 30, 0, 0, 0), zone);

        // A single AeroAPI query for the correct window would return exactly the rows that fall
        // inside it, drawn from the two recorded naive-query fixtures: the early departures from
        // the "day" fixture (which the buggy [D,D+1) UTC query got right) plus the late departures
        // from the "daybefore" fixture (which the buggy query missed entirely).
        var dayRows = ReadRows(ReadFixture("schedules-utc-window-day.json"));
        var dayBeforeRows = ReadRows(ReadFixture("schedules-utc-window-daybefore.json"));
        var correctRows = dayRows.Where(r => r.ScheduledOutUtc < expectedEnd)
            .Concat(dayBeforeRows.Where(r => r.ScheduledOutUtc >= expectedStart))
            .ToList();

        // Sanity check on the fixtures themselves: this is exactly the bug the design doc measured.
        Assert.Equal(11, correctRows.Count);
        var missedByTheNaiveQuery = new[] { "NZ101", "QF140", "QF4", "NZ103" };
        Assert.All(missedByTheNaiveQuery, ident => Assert.Contains(correctRows, r => r.Ident == ident));
        var wronglyIncludedByTheNaiveQuery = new[] { "JQ202", "QF142" };
        Assert.All(wronglyIncludedByTheNaiveQuery, ident => Assert.DoesNotContain(correctRows, r => r.Ident == ident));

        var body = ToScheduledBody(correctRows);
        var handler = new ScriptedHttpMessageHandler((HttpStatusCode.OK, body));
        var timezones = TimezoneLookup(("AKL", "Pacific/Auckland"));

        var results = await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria("AKL", "SYD", new DateOnly(2026, 9, 29), null));

        Assert.Single(handler.Requests);
        Assert.Contains(expectedStart.ToString("yyyy-MM-ddTHH:mm:ssZ"), handler.Requests[0].AbsolutePath);
        Assert.Contains(expectedEnd.ToString("yyyy-MM-ddTHH:mm:ssZ"), handler.Requests[0].AbsolutePath);

        Assert.Equal(11, results.Count);
        Assert.All(missedByTheNaiveQuery, ident => Assert.Contains(results, r => r.FlightNumbers.Single() == ident));
    }

    [Fact]
    public async Task SearchAsync_WidensWindowAndFiltersPerRow_WhenNoOriginIsGiven()
    {
        // A flight-number-only search may have no known origin at all — AeroAPI accepts
        // airline+flight_number without one. There is nothing to resolve a zone for up front, so
        // the query widens to a 3-day UTC window and each returned row is filtered against ITS OWN
        // origin_iata's local date afterward.
        const string body = """
        {
          "scheduled": [
            { "ident_iata": "TK1028", "actual_ident_iata": null, "origin_iata": "SOF", "destination_iata": "IST",
              "scheduled_out": "2026-09-29T06:35:00Z", "scheduled_in": "2026-09-29T08:05:00Z", "aircraft_type": "A321" },
            { "ident_iata": "TK1028", "actual_ident_iata": null, "origin_iata": "SOF", "destination_iata": "IST",
              "scheduled_out": "2026-09-28T06:35:00Z", "scheduled_in": "2026-09-28T08:05:00Z", "aircraft_type": "A321" }
          ],
          "links": null,
          "num_pages": 1
        }
        """;
        var handler = new ScriptedHttpMessageHandler((HttpStatusCode.OK, body));
        var timezones = TimezoneLookup(("SOF", "Europe/Sofia"));

        var results = await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria(null, null, new DateOnly(2026, 9, 29), "TK1028"));

        var result = Assert.Single(results);
        Assert.Equal(new DateTime(2026, 9, 29, 6, 35, 0, DateTimeKind.Utc), result.ScheduledDepartureUtc);

        // No origin filter could be pushed down, so the request carries none.
        Assert.DoesNotContain("origin=", handler.Requests[0].Query);
    }

    [Fact]
    public async Task SearchAsync_KeepsTheRow_WhenTheOriginZoneNeverResolves()
    {
        // #275 established the principle for AviationStack: an unplaceable time is worse omitted
        // than approximated. The AeroAPI provider keeps the same contract for its own degraded
        // path — falling back to a UTC-calendar-date comparison rather than dropping the row.
        const string body = """
        {
          "scheduled": [
            { "ident_iata": "XX100", "actual_ident_iata": null, "origin_iata": "ZZZ", "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T06:00:00Z", "scheduled_in": "2026-09-29T12:00:00Z" }
          ],
          "links": null,
          "num_pages": 1
        }
        """;
        var handler = new ScriptedHttpMessageHandler((HttpStatusCode.OK, body));
        // "ZZZ" is never in the zone map, so every lookup for it (window resolution AND the
        // per-row filter) returns null - a persistently unresolvable airport.
        var timezones = TimezoneLookup();

        var results = await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria("ZZZ", "JFK", new DateOnly(2026, 9, 29), null));

        Assert.Single(results);
    }

    // ------------------------------------------------------------------
    // Paging
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_FollowsLinksNext_AcrossPages()
    {
        const string page2 = """
        {
          "scheduled": [
            { "ident_iata": "BA178", "actual_ident_iata": null, "origin_iata": "LHR", "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T10:00:00Z", "scheduled_in": "2026-09-29T18:00:00Z", "aircraft_type": "B77W" }
          ],
          "links": null,
          "num_pages": 1
        }
        """;
        var handler = new ScriptedHttpMessageHandler(
            (HttpStatusCode.OK, ReadFixture("schedules-route-paged.json")),
            (HttpStatusCode.OK, page2));
        var timezones = TimezoneLookup(("LHR", "Europe/London"));

        var results = await Provider(handler, timezones, maxPages: 4).SearchAsync(
            new FlightSearchCriteria("LHR", "JFK", new DateOnly(2026, 9, 29), null));

        Assert.Equal(2, handler.Requests.Count);
        // The "next" link in the fixture is rooted at the API host ("/schedules/...") — it must be
        // combined against the AeroApi client's BaseAddress (which already ends "/aeroapi/"), not
        // treated as absolute from the domain root, or the request would 404 in production.
        Assert.StartsWith("http://stub/aeroapi/schedules/", handler.Requests[1].ToString());
        Assert.Contains("cursor=a3b0fa698216c28", handler.Requests[1].Query);

        Assert.Equal(16, results.Count); // 15 from page 1 + 1 from page 2
    }

    [Fact]
    public async Task SearchAsync_StopsAtMaxPages_WhenMoreResultsRemain()
    {
        // Every response carries a "next" link, simulating an endpoint that would page forever.
        var handler = new ScriptedHttpMessageHandler(
            (HttpStatusCode.OK, ReadFixture("schedules-route-paged.json")));
        var timezones = TimezoneLookup(("LHR", "Europe/London"));

        var results = await Provider(handler, timezones, maxPages: 1).SearchAsync(
            new FlightSearchCriteria("LHR", "JFK", new DateOnly(2026, 9, 29), null));

        Assert.Single(handler.Requests); // capped at 1 page, never followed "next"
        Assert.Equal(15, results.Count);
    }

    // ------------------------------------------------------------------
    // Rejection of an unfiltered airport query
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_RejectsAnOriginOnlySearch_WithNoDestinationOrFlightNumber()
    {
        var handler = new ScriptedHttpMessageHandler((HttpStatusCode.OK, """{ "scheduled": [] }"""));

        var ex = await Assert.ThrowsAsync<DomainException>(() => Provider(handler).SearchAsync(
            new FlightSearchCriteria("LHR", null, new DateOnly(2026, 9, 29), null)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Empty(handler.Requests); // rejected before any HTTP call
    }

    // ------------------------------------------------------------------
    // DST edge handling for local-midnight window boundaries
    // ------------------------------------------------------------------

    /// <summary>A synthetic zone whose transitions land exactly on local midnight — deliberately
    /// unlike real-world zones (which transition at 1-3am precisely to avoid this) — so both DST
    /// edge cases can be forced on demand.</summary>
    private static TimeZoneInfo MidnightTransitionZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 15),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1));

        return TimeZoneInfo.CreateCustomTimeZone(
            "Test/MidnightTransition", TimeSpan.FromHours(2), "Midnight Transition Test Zone",
            "STD", "DST", [rule]);
    }

    [Fact]
    public void ResolveLocalToUtc_AdvancesToTheFirstValidInstant_WhenLocalMidnightIsSkipped()
    {
        var zone = MidnightTransitionZone();
        // 2026-03-15T00:00 does not exist in this zone: clocks spring forward straight to 01:00.
        var localMidnight = new DateTime(2026, 3, 15, 0, 0, 0);
        Assert.True(zone.IsInvalidTime(localMidnight));

        var utc = AeroApiFlightSearchProvider.ResolveLocalToUtc(zone, localMidnight, preferLater: false);

        var expected = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 3, 15, 1, 0, 0), zone);
        Assert.Equal(expected, utc);
    }

    [Fact]
    public void ResolveLocalToUtc_PicksTheEarlierOrLaterInstant_WhenLocalMidnightIsAmbiguous()
    {
        var zone = MidnightTransitionZone();
        // 2026-11-01T00:00 occurs twice here: once under daylight offset, once under standard.
        var localMidnight = new DateTime(2026, 11, 1, 0, 0, 0);
        Assert.True(zone.IsAmbiguousTime(localMidnight));

        var earlier = AeroApiFlightSearchProvider.ResolveLocalToUtc(zone, localMidnight, preferLater: false);
        var later = AeroApiFlightSearchProvider.ResolveLocalToUtc(zone, localMidnight, preferLater: true);

        // A window start should take the earlier instant (never excludes anything near the
        // boundary); a window end should take the later one — both err towards inclusion.
        Assert.True(earlier < later);
        Assert.Equal(TimeSpan.FromHours(1), later - earlier);
    }

    // ------------------------------------------------------------------
    // Fixture parsing helpers (test-only; independent of the provider's own JSON mapping)
    // ------------------------------------------------------------------

    private sealed record FixtureRow(string Ident, DateTime ScheduledOutUtc, string RawJson);

    private static List<FixtureRow> ReadRows(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("scheduled").EnumerateArray()
            .Select(e => new FixtureRow(
                e.GetProperty("ident_iata").GetString()!,
                DateTime.Parse(e.GetProperty("scheduled_out").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                e.GetRawText()))
            .ToList();
    }

    private static string ToScheduledBody(IEnumerable<FixtureRow> rows) =>
        $$"""{ "scheduled": [{{string.Join(",", rows.Select(r => r.RawJson))}}], "links": null, "num_pages": 1 }""";
}
