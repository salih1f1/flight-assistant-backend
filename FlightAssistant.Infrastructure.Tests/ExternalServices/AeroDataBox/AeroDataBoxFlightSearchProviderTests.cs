using System.Globalization;
using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroDataBox;

/// <summary>
/// Drives <see cref="AeroDataBoxFlightSearchProvider"/> against two fixtures: <c>departures.json</c>
/// is hand-authored in the shape AeroDataBox's <c>/flights/airports/iata/...</c> endpoint was
/// believed to return during the 2026-09-16 spike — it is not a recorded payload — and
/// <c>departures-sof-2026-09-18-recorded.json</c> is the real thing: SOF's departures for
/// 2026-09-18, copied verbatim from a live call made during the #422 final-review fix wave
/// (2026-09-17). The recorded fixture is what caught the hand-authored one's central wrong guess —
/// <c>departure.airport</c> is absent from every one of its 36 real rows, where the hand-authored
/// fixture had assumed it was always present. Both mirror <c>AeroApiFlightSearchProviderTests</c>'s
/// scripted-handler-plus-fixture approach. No live call is ever made here and the configured key is
/// always a dummy value that must never appear in any assertion or log output. Each hand-authored
/// row's <c>aircraft</c> object is shape-filler only — its <c>model</c>/<c>reg</c> sub-fields were
/// not recorded from either spike and nothing in the mapping reads them.
/// </summary>
public class AeroDataBoxFlightSearchProviderTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AeroDataBox");

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixturesDir, name));

    private const string EmptyDeparturesBody = """{ "departures": [] }""";

    /// <summary>
    /// The provider issues two sequential window requests per search (see the provider's
    /// <c>DayWindows</c>). Serving the SAME body to both would double every row, making dedupe
    /// load-bearing in tests that are not about dedupe — so this default handler answers the FIRST
    /// request with the scripted <c>(status, body)</c> and every request after that with an
    /// ordinary empty-departures <c>200 OK</c>. Tests that need to script both windows
    /// independently use <see cref="PerWindowHttpMessageHandler"/> instead.
    /// </summary>
    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Dictionary<string, string>? _headers;

        public ScriptedHttpMessageHandler(HttpStatusCode status, string body, Dictionary<string, string>? headers = null)
        {
            _status = status;
            _body = body;
            _headers = headers;
        }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var isFirst = Requests.Count == 1;
            var response = new HttpResponseMessage(isFirst ? _status : HttpStatusCode.OK)
            {
                Content = new StringContent(isFirst ? _body : EmptyDeparturesBody, Encoding.UTF8, "application/json")
            };
            if (isFirst && _headers is not null)
            {
                foreach (var (key, value) in _headers)
                    response.Headers.TryAddWithoutValidation(key, value);
            }
            return Task.FromResult(response);
        }
    }

    /// <summary>Answers each request by inspecting its URL — used to give the two search windows
    /// different responses (merge, dedupe, partial-failure tests).</summary>
    private sealed class PerWindowHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<Uri, (HttpStatusCode Status, string Body)> _responseFor;
        public List<Uri> Requests { get; } = [];

        public PerWindowHttpMessageHandler(Func<Uri, (HttpStatusCode, string)> responseFor) => _responseFor = responseFor;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var (status, body) = _responseFor(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Throws on every request, to simulate the host being unreachable.</summary>
    private sealed class UnreachableHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated connection failure");
    }

    /// <summary>Throws a given exception on every request — used to simulate
    /// <c>HttpClient.Timeout</c>, this client's only timeout (it carries no resilience handler).</summary>
    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _factory;
        public ThrowingHttpMessageHandler(Func<Exception> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _factory();
    }

    private static (AeroDataBoxFlightSearchProvider Provider, ScriptedHttpMessageHandler Handler) Provider(
        HttpStatusCode status, string body, Dictionary<string, string>? headers = null, string? apiKey = "test-dummy-key")
    {
        var handler = new ScriptedHttpMessageHandler(status, body, headers);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        var provider = new AeroDataBoxFlightSearchProvider(
            http,
            Options.Create(new AeroDataBoxOptions { ApiKey = apiKey }),
            NullLogger<AeroDataBoxFlightSearchProvider>.Instance);
        return (provider, handler);
    }

    private static AeroDataBoxFlightSearchProvider ProviderOver(HttpMessageHandler handler, string? apiKey = "test-dummy-key")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        return new AeroDataBoxFlightSearchProvider(
            http,
            Options.Create(new AeroDataBoxOptions { ApiKey = apiKey }),
            NullLogger<AeroDataBoxFlightSearchProvider>.Instance);
    }

    private static readonly FlightSearchCriteria SofToIst =
        new("SOF", "IST", new DateOnly(2026, 9, 29), null, IncludeCodeshares: true);

    private static string Departure(string number, string codeshareStatus, string scheduledUtc, string scheduledLocal) => $$"""
        {
          "number": "{{number}}",
          "codeshareStatus": "{{codeshareStatus}}",
          "departure": { "airport": { "iata": "SOF" }, "scheduledTime": { "utc": "{{scheduledUtc}}", "local": "{{scheduledLocal}}" } },
          "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-29T08:05:00Z", "local": "2026-09-29T11:05:00+03:00" } }
        }
        """;

    // ------------------------------------------------------------------
    // The request URL: two ≤12-hour windows, not one whole day
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_QueriesTwoWindows_WithCodesharedTrue()
    {
        var (provider, handler) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        await provider.SearchAsync(SofToIst with { IncludeCodeshares = true });

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/flights/airports/iata/SOF/2026-09-29T00:00/2026-09-29T11:59", handler.Requests[0].AbsolutePath);
        Assert.Equal("/flights/airports/iata/SOF/2026-09-29T12:00/2026-09-29T23:59", handler.Requests[1].AbsolutePath);
        Assert.All(handler.Requests, r =>
        {
            Assert.Contains("withLeg=true", r.Query);
            Assert.Contains("direction=Departure", r.Query);
            Assert.Contains("withCancelled=false", r.Query);
            Assert.Contains("withCargo=false", r.Query);
            Assert.Contains("withPrivate=false", r.Query);
            Assert.Contains("withCodeshared=true", r.Query);
        });
    }

    [Fact]
    public async Task SearchAsync_QueriesTwoWindows_WithCodesharedFalse()
    {
        var (provider, handler) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        await provider.SearchAsync(SofToIst with { IncludeCodeshares = false });

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("withCodeshared=false", r.Query));
    }

    [Fact]
    public async Task SearchAsync_NeverRequestsAWindowLongerThan12Hours()
    {
        // Regression guard for #267 (f89f95e's test did the same): AeroDataBox's own parameter
        // documentation says a window over 12 hours is rejected — #267 quotes it, though the
        // removed provider never logged a response to confirm it (#422 final review, finding 5).
        var (provider, handler) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        await provider.SearchAsync(SofToIst);

        Assert.NotEmpty(handler.Requests);
        foreach (var uri in handler.Requests)
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var to = DateTime.ParseExact(segments[^1], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            var from = DateTime.ParseExact(segments[^2], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            Assert.True(to > from, "End of window must be after start.");
            Assert.True(to - from <= TimeSpan.FromHours(12),
                $"Window {from:o}..{to:o} exceeds the 12h AeroDataBox limit.");
        }
    }

    [Fact]
    public async Task SearchAsync_FormatsTheWindowDatesInvariantly_RegardlessOfCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var (provider, handler) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

            await provider.SearchAsync(SofToIst);

            Assert.All(handler.Requests, r => Assert.Contains("2026-09-29", r.AbsolutePath));
            Assert.All(handler.Requests, r => Assert.DoesNotContain("2569", r.AbsolutePath));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ------------------------------------------------------------------
    // Merge and dedupe across the two windows
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_MergesRowsFromBothWindows()
    {
        // Window 1 (…T11:59) answers with a morning row; window 2 (…T23:59) answers with an
        // afternoon one. Both must come back.
        var morning = $$"""{ "departures": [ {{Departure("TK 1028", "IsOperator", "2026-09-29T06:35:00Z", "2026-09-29T09:35:00+03:00")}} ] }""";
        var afternoon = $$"""{ "departures": [ {{Departure("TK 1032", "IsOperator", "2026-09-29T15:00:00Z", "2026-09-29T18:00:00+03:00")}} ] }""";
        var handler = new PerWindowHttpMessageHandler(uri =>
            uri.AbsolutePath.EndsWith("T11:59") ? (HttpStatusCode.OK, morning) : (HttpStatusCode.OK, afternoon));
        var provider = ProviderOver(handler);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FlightNumber == "TK1028");
        Assert.Contains(results, r => r.FlightNumber == "TK1032");
    }

    [Fact]
    public async Task SearchAsync_DedupesTheSameRowFromBothWindows_ButKeepsACodeshareAtTheSameTime()
    {
        // Both windows answer identically here, simulating the same physical departures showing up
        // in both responses (e.g. a boundary overlap). TK1028 must be deduped to one row; its
        // codeshare 6E4120 — same scheduled time, different flight number — must survive
        // separately, since the dedupe key is (FlightNumber, ScheduledDepartureUtc).
        var body = $$"""
        {
          "departures": [
            {{Departure("TK 1028", "IsOperator", "2026-09-29T06:35:00Z", "2026-09-29T09:35:00+03:00")}},
            {{Departure("6E 4120", "IsCodeshared", "2026-09-29T06:35:00Z", "2026-09-29T09:35:00+03:00")}}
          ]
        }
        """;
        var handler = new PerWindowHttpMessageHandler(_ => (HttpStatusCode.OK, body));
        var provider = ProviderOver(handler);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Equal(2, results.Count);
        Assert.Single(results, r => r.FlightNumber == "TK1028");
        Assert.Single(results, r => r.FlightNumber == "6E4120");
    }

    [Fact]
    public async Task SearchAsync_KeepsBothRows_WhenTheSameFlightNumberHasDifferentScheduledTimes()
    {
        // The dedupe key is (FlightNumber, ScheduledDepartureUtc) — the test above only proves the
        // flight-number half discriminates (two different numbers, same time). This proves the time
        // half discriminates too: the SAME flight number at two different scheduled departure times
        // must both survive, not collapse into one (#422 fix round 3). Mutating the key
        // to FlightNumber alone passes every other test in this class but fails this one.
        var body = $$"""
        {
          "departures": [
            {{Departure("TK 1028", "IsOperator", "2026-09-29T06:35:00Z", "2026-09-29T09:35:00+03:00")}},
            {{Departure("TK 1028", "IsOperator", "2026-09-29T09:00:00Z", "2026-09-29T12:00:00+03:00")}}
          ]
        }
        """;
        var handler = new PerWindowHttpMessageHandler(_ => (HttpStatusCode.OK, body));
        var provider = ProviderOver(handler);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Equal(2, results.Count(r => r.FlightNumber == "TK1028"));
        Assert.Contains(results, r => r.FlightNumber == "TK1028" && r.ScheduledDepartureUtc == new DateTime(2026, 9, 29, 6, 35, 0, DateTimeKind.Utc));
        Assert.Contains(results, r => r.FlightNumber == "TK1028" && r.ScheduledDepartureUtc == new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SearchAsync_Throws_WhenTheSecondWindowFails_AndReturnsNoRows()
    {
        // Window 1 succeeds with a row; window 2 fails. The whole search must throw — one window's
        // rows are never returned as if they were the full day.
        var morning = $$"""{ "departures": [ {{Departure("TK 1028", "IsOperator", "2026-09-29T06:35:00Z", "2026-09-29T09:35:00+03:00")}} ] }""";
        var handler = new PerWindowHttpMessageHandler(uri =>
            uri.AbsolutePath.EndsWith("T11:59")
                ? (HttpStatusCode.OK, morning)
                : (HttpStatusCode.InternalServerError, """{ "message": "boom" }"""));
        var provider = ProviderOver(handler);

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_NeverSendsTheSecondWindow_WhenTheFirstWindowFails()
    {
        // #422 final review, finding 10: "up to two sequential calls" and the early exit are both
        // pinned by request COUNT, not just by the thrown status — a handler that answered window 2
        // anyway would still pass a test that only checks the exception.
        var handler = new PerWindowHttpMessageHandler(_ =>
            (HttpStatusCode.InternalServerError, """{ "message": "boom" }"""));
        var provider = ProviderOver(handler);

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
        Assert.Single(handler.Requests);
        Assert.EndsWith("T11:59", handler.Requests[0].AbsolutePath);
    }

    // ------------------------------------------------------------------
    // The recorded payload (#422 final review, live measurement 2026-09-17): the OLD code required
    // departure.airport.iata, which is absent from all 36 real rows here, so every one of these
    // assertions is genuinely red against that code — the whole search returns [] instead.
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_MapsTheRecordedSofDepartures_SofToVie_DedupingTheGenuineDuplicate()
    {
        var handler = new PerWindowHttpMessageHandler(uri =>
            uri.AbsolutePath.EndsWith("T11:59")
                ? (HttpStatusCode.OK, ReadFixture("departures-sof-2026-09-18-recorded.json"))
                : (HttpStatusCode.NoContent, ""));
        var provider = ProviderOver(handler);
        var criteria = new FlightSearchCriteria("SOF", "VIE", new DateOnly(2026, 9, 18), null, IncludeCodeshares: true);

        var results = await provider.SearchAsync(criteria);

        Assert.Equal(2, results.Count);

        var os780 = Assert.Single(results, r => r.FlightNumber == "OS780");
        Assert.Equal(new DateTime(2026, 9, 18, 5, 0, 0, DateTimeKind.Utc), os780.ScheduledDepartureUtc);
        Assert.Equal(new DateTime(2026, 9, 18, 6, 35, 0, DateTimeKind.Utc), os780.ScheduledArrivalUtc);
        Assert.Equal(new DateOnly(2026, 9, 18), os780.ServiceDate);
        Assert.Equal("SOF", os780.DepartureAirportIata);
        Assert.Equal("VIE", os780.ArrivalAirportIata);

        var fr183 = Assert.Single(results, r => r.FlightNumber == "FR183");
        Assert.Equal(new DateOnly(2026, 9, 18), fr183.ServiceDate);
    }

    [Fact]
    public async Task SearchAsync_MapsTheRecordedSofDepartures_SofToBeg()
    {
        var handler = new PerWindowHttpMessageHandler(uri =>
            uri.AbsolutePath.EndsWith("T11:59")
                ? (HttpStatusCode.OK, ReadFixture("departures-sof-2026-09-18-recorded.json"))
                : (HttpStatusCode.NoContent, ""));
        var provider = ProviderOver(handler);
        var criteria = new FlightSearchCriteria("SOF", "BEG", new DateOnly(2026, 9, 18), null, IncludeCodeshares: true);

        var results = await provider.SearchAsync(criteria);

        var ju117 = Assert.Single(results, r => r.FlightNumber == "JU117");
        Assert.Equal(new DateTime(2026, 9, 18, 2, 0, 0, DateTimeKind.Utc), ju117.ScheduledDepartureUtc);
        Assert.Equal(new DateTime(2026, 9, 18, 3, 20, 0, DateTimeKind.Utc), ju117.ScheduledArrivalUtc);
    }

    // ------------------------------------------------------------------
    // Mapping: codeshares pass through, destination filtered client-side
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_KeepsCodeshareRows_ItDoesNotFilterOnCodeshareStatusClientSide()
    {
        // The SOF caution: a quiet window can return identical results for withCodeshared=false vs
        // true, which looks like "the flag does nothing" when really the window just had no
        // codeshares. This fixture actually contains one (6E 4120, IsCodeshared, alongside TK 1028,
        // IsOperator, for the same physical departure) so this test can fail if the mapping ever
        // starts dropping codeshare rows.
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(SofToIst);

        Assert.Contains(results, r => r.FlightNumber == "TK1028");
        Assert.Contains(results, r => r.FlightNumber == "6E4120");
    }

    [Fact]
    public async Task SearchAsync_FiltersRowsToTheRequestedDestination_ClientSide()
    {
        // AeroDataBox has no destination filter server-side — the fixture's "BA 249" row departs
        // SOF but arrives LHR, not the requested IST, and must not appear in the results.
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(SofToIst);

        Assert.DoesNotContain(results, r => r.FlightNumber == "BA249");
        Assert.All(results, r => Assert.Equal("IST", r.ArrivalAirportIata));
        // Positive anchor — without it this test passes against `return [];` too.
        Assert.Contains(results, r => r.FlightNumber == "TK1028");
    }

    [Fact]
    public async Task SearchAsync_TrimsTheArrivalIata_BeforeComparing()
    {
        // The origin is trimmed and uppercased in BuildUrl already; the arrival, compared only in
        // TryMapRow, needs the same trim — otherwise a padded FlightSearchCriteria.ArrivalIata
        // returns zero rows silently (#422 fix round 2).
        var padded = SofToIst with { ArrivalIata = " IST " };
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(padded);

        Assert.Contains(results, r => r.FlightNumber == "TK1028");
    }

    [Fact]
    public async Task SearchAsync_MapsFlightNumber_StrippingTheSpaceAeroDataBoxReturns()
    {
        // Measured: AeroDataBox's "number" field is space-separated ("TK 1028"), but the domain
        // format (FlightNumberFormat) and every other provider have no space ("TK1028").
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(SofToIst);

        Assert.Contains(results, r => r.FlightNumber == "TK1028");
        Assert.DoesNotContain(results, r => r.FlightNumber!.Contains(' '));
    }

    [Fact]
    public async Task SearchAsync_SetsServiceDateToTheRequestedDate_AndUsesScheduledUtcTimes()
    {
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(SofToIst);
        var tk1028 = Assert.Single(results, r => r.FlightNumber == "TK1028");

        Assert.Equal(new DateOnly(2026, 9, 29), tk1028.ServiceDate);
        Assert.Equal("SOF", tk1028.DepartureAirportIata);
        Assert.Equal("IST", tk1028.ArrivalAirportIata);
        Assert.Equal(new DateTime(2026, 9, 29, 6, 35, 0, DateTimeKind.Utc), tk1028.ScheduledDepartureUtc);
        Assert.Equal(new DateTime(2026, 9, 29, 8, 5, 0, DateTimeKind.Utc), tk1028.ScheduledArrivalUtc);
    }

    // ------------------------------------------------------------------
    // The scheduled-date keeper — never the revised time (#422 fix round 1, unchanged)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_KeepsARowMerelyDelayedWithinTheRequestedDay()
    {
        // TK 1030: scheduled 09:00 local, revised to 09:26 local — still 2026-09-29. Must be kept,
        // and its ScheduledDepartureUtc must be the SCHEDULED time (06:00Z), not the revised one.
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(SofToIst);
        var tk1030 = Assert.Single(results, r => r.FlightNumber == "TK1030");

        Assert.Equal(new DateTime(2026, 9, 29, 6, 0, 0, DateTimeKind.Utc), tk1030.ScheduledDepartureUtc);
    }

    [Fact]
    public async Task SearchAsync_KeepsARowScheduledOnTheRequestedDay_EvenWhenRevisedPastLocalMidnight()
    {
        // TK 1032: scheduled 23:50 local on the 29th, revised to 00:20 local on the 30th. Its
        // revised time (00:20 on the 30th) falls after the end of the LAST window this provider
        // ever queries for this date (2026-09-29T23:59), so the real AeroDataBox endpoint cannot
        // actually return this row for this search — see the class remarks' residual-cost
        // paragraph. This test pins the predicate against a synthetic row instead: IF a row like
        // this were ever returned, it must be kept, because it is a 29th service — SCHEDULED on the
        // 29th, whatever its delay does. It is the only test that separates "scheduled date" from
        // "scheduled AND revised dates both on the requested day" (TK1030 has both on the 29th;
        // TK1026 below has both off the 29th).
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(SofToIst);
        var tk1032 = Assert.Single(results, r => r.FlightNumber == "TK1032");

        Assert.Equal(new DateOnly(2026, 9, 29), tk1032.ServiceDate);
        Assert.Equal(new DateTime(2026, 9, 29, 20, 50, 0, DateTimeKind.Utc), tk1032.ScheduledDepartureUtc);
    }

    [Fact]
    public async Task SearchAsync_ExcludesARowScheduledOnThePreviousDay_ThatRevisedIntoTheRequestedDay()
    {
        // TK 1026: scheduled 23:40 local on the 28th, revised to 00:30 local on the 29th. The
        // 2026-09-16 spike measured only that a row revised OUT of a window is absent — not that a
        // row revised INTO one is present — so this is not confirmed to be a response AeroDataBox
        // would actually send. It CAN leak in if the server's revised-time window admits rows
        // revised into it as well as excludes rows revised out of it (unmeasured either way), but
        // the scheduled-date filter here is correct regardless: its scheduled local date is the
        // 28th, so it is excluded whether or not the server ever actually includes it.
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(SofToIst);

        Assert.DoesNotContain(results, r => r.FlightNumber == "TK1026");
        // Positive anchor — without it this test passes against `return [];` too.
        Assert.Contains(results, r => r.FlightNumber == "TK1028");
    }

    // ------------------------------------------------------------------
    // Malformed / empty responses
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_Maps502_WhenDeparturesArrayIsMissing()
    {
        // #422 final review, finding 6: the measured "no data" signal is a 204 (a quiet window AND
        // an unknown airport both answered 204, never a 200 with some other shape). A 200 object
        // with no "departures" array at all is not that — it is an unrecognised response shape, and
        // must be loud rather than read as "zero flights".
        var (provider, _) = Provider(HttpStatusCode.OK, """{ "somethingElse": [] }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyList_WhenDeparturesArrayIsEmpty()
    {
        var (provider, _) = Provider(HttpStatusCode.OK, """{ "departures": [] }""");

        var results = await provider.SearchAsync(SofToIst);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"oops\"")]
    public async Task SearchAsync_Maps502_WhenTheBodyParsesButIsNotAnObject(string body)
    {
        // Same finding-6 ruling as above: only 204 and {"departures":[…]} count as "no data";
        // everything else a 200 status could carry is an unrecognised shape, mapped loudly to 502.
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_SkipsNonObjectElementsOfDepartures()
    {
        const string body = """
        {
          "departures": [
            null,
            {
              "number": "TK 1028",
              "codeshareStatus": "IsOperator",
              "departure": { "airport": { "iata": "SOF" }, "scheduledTime": { "utc": "2026-09-29T06:35:00Z", "local": "2026-09-29T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-29T08:05:00Z", "local": "2026-09-29T11:05:00+03:00" } }
            }
          ]
        }
        """;
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Contains(results, r => r.FlightNumber == "TK1028");
    }

    [Fact]
    public async Task SearchAsync_Throws502_WhenDeparturesIsNonEmpty_ButEveryElementIsNonObject()
    {
        // Closes a gap in the all-unmappable guard (residual #422 fix): totalRows used to skip a
        // non-object array element entirely (`continue` before the increment), so a departures
        // array containing ONLY non-object elements left totalRows at 0 and the `totalRows > 0`
        // guard below never engaged — the window silently returned [], cacheable as "no data" for
        // up to 12h. That is exactly the silent-shape-change failure the structurally-unmappable
        // guard exists to make loud.
        const string body = """{ "departures": [null, null] }""";
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_SkipsMalformedRows_RatherThanThrowing()
    {
        // TK 1031's departure carries an empty "airport" object — no iata at all. #422 final review
        // (finding 1, measured: departure.airport is absent from 36 of 36 real rows) means this is
        // no longer treated as malformed: the origin comes from the criteria ("SOF") when the row
        // doesn't carry its own, so TK 1031 is mapped and kept alongside TK 1028. Only the row with
        // no "number" and the row with no departure.scheduledTime remain genuinely unmappable.
        const string body = """
        {
          "departures": [
            {
              "number": "TK 1028",
              "codeshareStatus": "IsOperator",
              "departure": { "airport": { "iata": "SOF" }, "scheduledTime": { "utc": "2026-09-29T06:35:00Z", "local": "2026-09-29T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-29T08:05:00Z", "local": "2026-09-29T11:05:00+03:00" } }
            },
            {
              "codeshareStatus": "IsOperator",
              "departure": { "airport": { "iata": "SOF" }, "scheduledTime": { "utc": "2026-09-29T06:35:00Z", "local": "2026-09-29T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-29T08:05:00Z", "local": "2026-09-29T11:05:00+03:00" } }
            },
            {
              "number": "TK 1029",
              "codeshareStatus": "IsOperator",
              "departure": { "airport": { "iata": "SOF" } },
              "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-29T08:05:00Z", "local": "2026-09-29T11:05:00+03:00" } }
            },
            {
              "number": "TK 1031",
              "codeshareStatus": "IsOperator",
              "departure": { "airport": { }, "scheduledTime": { "utc": "2026-09-29T06:35:00Z", "local": "2026-09-29T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-29T08:05:00Z", "local": "2026-09-29T11:05:00+03:00" } }
            }
          ]
        }
        """;
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FlightNumber == "TK1028");
        Assert.Contains(results, r => r.FlightNumber == "TK1031");
    }

    [Fact]
    public async Task SearchAsync_DropsARow_WhenItsOwnDepartureAirportDisagreesWithTheRequestedOrigin()
    {
        // Defensive check only (#422 final review, finding 1): no real row has ever carried
        // departure.airport (measured 36 of 36 absent), but if one ever does and names a DIFFERENT
        // airport than the one actually queried, it is dropped rather than trusted — the same
        // bucket as a destination or date mismatch, not "structurally unmappable" (it does not
        // trip the all-rows-unmappable 502 below).
        const string body = """
        {
          "departures": [
            {
              "number": "TK 1028",
              "codeshareStatus": "IsOperator",
              "departure": { "airport": { "iata": "OTP" }, "scheduledTime": { "utc": "2026-09-29T06:35:00Z", "local": "2026-09-29T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-29T08:05:00Z", "local": "2026-09-29T11:05:00+03:00" } }
            }
          ]
        }
        """;
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_Throws502_WhenDeparturesIsNonEmpty_ButNoRowIsStructurallyMappable()
    {
        // #422 final review, finding 1: "structurally unmappable" means a row lacks a field the
        // mapping needs — here, every row is missing arrival.scheduledTime entirely. That is a
        // shape change, and must be loud (502 + Error log), never cached as an empty "no data"
        // result the way a destination or date mismatch legitimately is.
        const string body = """
        {
          "departures": [
            {
              "number": "TK 1028",
              "codeshareStatus": "IsOperator",
              "departure": { "scheduledTime": { "utc": "2026-09-29T06:35:00Z", "local": "2026-09-29T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" } }
            },
            {
              "number": "TK 1029",
              "codeshareStatus": "IsOperator",
              "departure": { "scheduledTime": { "utc": "2026-09-29T07:00:00Z", "local": "2026-09-29T10:00:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" } }
            }
          ]
        }
        """;
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
        // The exact count phrase, not just "2" — DiagnosticDetail also embeds the search date
        // (2026-09-29), so a bare Assert.Contains("2", ...) would pass even if the interpolated row
        // count were wrong, since "2" appears in "2026" regardless of totalRows.
        Assert.Contains("returned 2 departures rows", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyList_WhenEveryRowIsStructurallyValidButHasADifferentArrivalAirport()
    {
        // Permanent misfire guard for the all-unmappable rule (residual #422 fix): every row here
        // is structurally complete (number, departure/arrival sections, arrival airport, both
        // scheduled times, a parseable scheduled local date) but is filtered out because none of
        // them is bound for the requested arrival — a legitimate "not this row" outcome, never a
        // structural-mapping failure. A window where every row is filtered this way must still
        // return an empty list, not 502.
        const string body = """
        {
          "departures": [
            {
              "number": "BA 249",
              "codeshareStatus": "IsOperator",
              "departure": { "airport": { "iata": "SOF" }, "scheduledTime": { "utc": "2026-09-29T06:35:00Z", "local": "2026-09-29T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "LHR" }, "scheduledTime": { "utc": "2026-09-29T09:05:00Z", "local": "2026-09-29T10:05:00+01:00" } }
            }
          ]
        }
        """;
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyList_WhenEveryRowIsStructurallyValidButHasADifferentScheduledLocalDate()
    {
        // Permanent misfire guard, the date-filter half: every row is structurally complete but its
        // own scheduled local date is the 28th, not the requested 29th — a legitimate date-filter
        // exclusion, never a structural-mapping failure. Must return an empty list, not 502.
        const string body = """
        {
          "departures": [
            {
              "number": "TK 1028",
              "codeshareStatus": "IsOperator",
              "departure": { "airport": { "iata": "SOF" }, "scheduledTime": { "utc": "2026-09-28T06:35:00Z", "local": "2026-09-28T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-28T08:05:00Z", "local": "2026-09-28T11:05:00+03:00" } }
            }
          ]
        }
        """;
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_KeepsTheMappableRows_WhenOnlySomeRowsAreStructurallyUnmappable()
    {
        // The partial-failure half of finding 1: SOME rows missing a required field must not take
        // down the whole window (that would be its own kind of silent-half-day answer if it threw
        // for one bad row among many good ones) — they are dropped and the rest kept.
        const string body = """
        {
          "departures": [
            {
              "number": "TK 1028",
              "codeshareStatus": "IsOperator",
              "departure": { "scheduledTime": { "utc": "2026-09-29T06:35:00Z", "local": "2026-09-29T09:35:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" }, "scheduledTime": { "utc": "2026-09-29T08:05:00Z", "local": "2026-09-29T11:05:00+03:00" } }
            },
            {
              "number": "TK 1029",
              "codeshareStatus": "IsOperator",
              "departure": { "scheduledTime": { "utc": "2026-09-29T07:00:00Z", "local": "2026-09-29T10:00:00+03:00" } },
              "arrival": { "airport": { "iata": "IST" } }
            }
          ]
        }
        """;
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var results = await provider.SearchAsync(SofToIst);

        var kept = Assert.Single(results);
        Assert.Equal("TK1028", kept.FlightNumber);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyList_OnNoContentSuccess()
    {
        // Defensive and unobserved — no spike ever hit an empty window, but the global constraint
        // is "no data is an empty list, never an error", and 204 must not be treated as an
        // unparseable-body failure.
        var (provider, _) = Provider(HttpStatusCode.NoContent, "");

        var results = await provider.SearchAsync(SofToIst);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_Maps502_OnAnEmptyOrWhitespaceSuccessBody()
    {
        // An empty or whitespace 200 body is not valid JSON, so it is not "no data" either. Were it
        // read as empty, one window could answer "no flights" while the other returned rows,
        // presenting half a day as a full day (#422 fix round 3). A 204 is the only success status
        // read as "no data".
        var (provider, _) = Provider(HttpStatusCode.OK, "   ");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_ThrowsBadGateway_WhenTheBodyIsNotValidJson()
    {
        var (provider, _) = Provider(HttpStatusCode.OK, "not json");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_ThrowsBadGateway_WhenTheHostIsUnreachable()
    {
        var provider = ProviderOver(new UnreachableHttpMessageHandler());

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // Timeout vs genuine caller cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_MapsATimeout_ToBadGateway_WhenTheCallerDidNotCancel()
    {
        // HttpClient.Timeout throws TaskCanceledException — an OperationCanceledException — and
        // this client carries no resilience handler, so HttpClient.Timeout is its only timeout.
        var provider = ProviderOver(new ThrowingHttpMessageHandler(
            () => new TaskCanceledException("timeout", new TimeoutException())));

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
        Assert.IsType<TaskCanceledException>(ex.InnerException);
        Assert.Equal("AeroDataBox did not respond before the client timeout.", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task SearchAsync_PropagatesCancellation_WhenTheCallerActuallyCancelled()
    {
        var provider = ProviderOver(new ThrowingHttpMessageHandler(
            () => new TaskCanceledException("timeout", new TimeoutException())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchAsync(SofToIst, cts.Token));
    }

    // ------------------------------------------------------------------
    // Failure status mapping
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_Maps401_ToInternalServerError()
    {
        var (provider, _) = Provider(HttpStatusCode.Unauthorized, """{ "message": "Invalid API key" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Flight data is unavailable.", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_Maps403_ToInternalServerError()
    {
        var (provider, _) = Provider(HttpStatusCode.Forbidden, """{ "message": "not subscribed" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Flight data is unavailable.", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_Maps429_ToServiceUnavailable_WithRetryAfter()
    {
        var (provider, _) = Provider(
            HttpStatusCode.TooManyRequests,
            """{ "message": "too many requests" }""",
            new Dictionary<string, string> { ["Retry-After"] = "30" });

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("30s", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_Maps400_ToInternalServerError_WithoutInvitingARetry()
    {
        // #267 quotes AeroDataBox's own parameter documentation ("not more than by 12 hours") as
        // the reason the removed whole-day window would have failed — but #267 also records that
        // the removed provider logged nothing, so the 400 itself was never observed, only inferred
        // from that documentation (#422 final review, finding 5). "Flight data is unavailable."
        // (not "...temporarily unavailable.") because "temporarily" implies waiting helps, which is
        // false for a request our own code built wrong — the precedent is GitHubCiRunProvider's "CI
        // results are unavailable." for its own configuration-caused 500s (#422 fix round 3).
        var (provider, _) = Provider(HttpStatusCode.BadRequest, """{ "message": "range too long" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Flight data is unavailable.", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_Maps302FromUpstream_ToBadGateway()
    {
        // #422 final review, finding 2: the named client's redirects are disabled at the transport
        // (DI-level; see AeroApiDependencyInjectionTests), so a 3xx response reaches this mapping
        // code exactly as AeroDataBox sent it, rather than being followed first. It is neither a
        // success, nor 401/403/429, nor a 4xx, so it falls into the same catch-all bucket as any
        // other unrecognised status.
        var (provider, _) = Provider(HttpStatusCode.Found, """{ "message": "redirecting" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_Maps404_ToInternalServerError()
    {
        var (provider, _) = Provider(HttpStatusCode.NotFound, """{ "message": "not found" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_Maps500FromUpstream_ToBadGateway()
    {
        var (provider, _) = Provider(HttpStatusCode.InternalServerError, """{ "message": "boom" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_Maps503FromUpstream_ToBadGateway()
    {
        // Upstream sending ITS OWN 503 is distinct from the 429 case we map to our own 503 — this
        // falls into the catch-all 5xx bucket, 502.
        var (provider, _) = Provider(HttpStatusCode.ServiceUnavailable, """{ "message": "maintenance" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(502, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // The API key must never leak into a diagnostic detail or a log
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_RedactsTheApiKey_FromTheDiagnosticDetail_IfEverEchoedBack()
    {
        // AeroDataBox auth travels only in the X-RapidAPI-Key request header and is never expected
        // to appear in a response body, but this proves the defense-in-depth redaction actually
        // works rather than merely existing in the code. The "key" here is a fixed test value, never
        // the real configured secret.
        const string configuredKey = "unit-test-fake-rapidapi-key";
        var (provider, _) = Provider(
            HttpStatusCode.Unauthorized,
            $$"""{ "message": "Invalid key: {{configuredKey}}" }""",
            apiKey: configuredKey);

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.DoesNotContain(configuredKey, ex.DiagnosticDetail);
        Assert.Contains("REDACTED", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task SearchAsync_RedactsOtherCredentialShapedFields_NotJustTheConfiguredKey()
    {
        // The test above only exercises the literal-configured-key replacement. This exercises
        // KeyLikeFieldPattern itself with a value that is NOT the configured key — a broken pattern
        // would not be caught by the test above.
        var (provider, _) = Provider(
            HttpStatusCode.Unauthorized,
            """{ "apiKey": "some-other-secret" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.DoesNotContain("some-other-secret", ex.DiagnosticDetail);
        Assert.Contains("REDACTED", ex.DiagnosticDetail);
    }

    // ------------------------------------------------------------------
    // Guard: a route search needs both airports — our defect, once Task 2 lands (#422 fix round 2)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, "IST")]
    [InlineData("SOF", null)]
    public async Task SearchAsync_RejectsASearchMissingEitherAirport(string? departureIata, string? arrivalIata)
    {
        var (provider, handler) = Provider(HttpStatusCode.OK, """{ "departures": [] }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(
            new FlightSearchCriteria(departureIata, arrivalIata, new DateOnly(2026, 9, 29), null)));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Flight data is unavailable.", ex.Message);
        Assert.Contains("both a departure and an arrival airport", ex.DiagnosticDetail);
        Assert.Empty(handler.Requests);
    }
}
