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
/// Drives <see cref="AeroDataBoxFlightSearchProvider"/> against a fixture hand-authored in the
/// shape AeroDataBox's <c>/flights/airports/iata/...</c> endpoint returned during the 2026-09-16
/// spike (see <c>FlightAssistant.Infrastructure.Tests/Fixtures/AeroDataBox/departures.json</c>) —
/// it is not a recorded payload — mirroring <c>AeroApiFlightSearchProviderTests</c>'s
/// scripted-handler-plus-fixture approach. No live call is ever made and the configured key here
/// is always a dummy value that must never appear in any assertion or log output. Each fixture
/// row's <c>aircraft</c> object is shape-filler only — its <c>model</c>/<c>reg</c> sub-fields were
/// not recorded from the spike and nothing in the mapping reads them.
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
        // Regression guard for #267 (f89f95e's test did the same): AeroDataBox rejects any window
        // over 12 hours with HTTP 400.
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
        // returns zero rows silently (fix round 2, finding 13).
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
    // The scheduled-date keeper — never the revised time (unchanged from fix round 1)
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
    public async Task SearchAsync_ReturnsEmptyList_WhenDeparturesArrayIsMissing()
    {
        var (provider, _) = Provider(HttpStatusCode.OK, """{ "somethingElse": [] }""");

        var results = await provider.SearchAsync(SofToIst);

        Assert.Empty(results);
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
    public async Task SearchAsync_ReturnsEmptyList_WhenTheBodyParsesButIsNotAnObject(string body)
    {
        var (provider, _) = Provider(HttpStatusCode.OK, body);

        var results = await provider.SearchAsync(SofToIst);

        Assert.Empty(results);
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
    public async Task SearchAsync_SkipsMalformedRows_RatherThanThrowing()
    {
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
    public async Task SearchAsync_ReturnsEmptyList_OnAnEmptyOrWhitespaceSuccessBody()
    {
        // Defensive and unobserved, same reasoning as the 204 case above.
        var (provider, _) = Provider(HttpStatusCode.OK, "   ");

        var results = await provider.SearchAsync(SofToIst);

        Assert.Empty(results);
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
    }

    [Fact]
    public async Task SearchAsync_Maps403_ToInternalServerError()
    {
        var (provider, _) = Provider(HttpStatusCode.Forbidden, """{ "message": "not subscribed" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(500, ex.StatusCode);
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
        // #267's live example: the removed whole-day window earned exactly this from AeroDataBox.
        var (provider, _) = Provider(HttpStatusCode.BadRequest, """{ "message": "range too long" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(SofToIst));

        Assert.Equal(500, ex.StatusCode);
        Assert.DoesNotContain("try again", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    // Guard: a route search needs both airports — our defect, once Task 2 lands (fix round 2, finding 9)
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
        Assert.Contains("both a departure and an arrival airport", ex.DiagnosticDetail);
        Assert.Empty(handler.Requests);
    }
}
