using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Drives <see cref="AeroApiFlightStatusProvider"/> against the recorded AeroAPI
/// <c>/flights/{ident}</c> payload for TK1028 (see
/// <c>FlightAssistant.Infrastructure.Tests/Fixtures/AeroApi/flight-status.json</c>), which spans
/// 14 rows of recent past and near future, plus synthetic bodies for status-prose variants the
/// fixture does not happen to contain.
/// </summary>
public class AeroApiFlightStatusProviderTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AeroApi");

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixturesDir, name));

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public Uri? LastRequestUri { get; private set; }
        public bool LastRequestSkippedRetries { get; private set; }

        public StubHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestSkippedRetries = AeroApiRetry.IsSkipped(request);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Records every log call made through it, so a test can assert a warning was
    /// actually emitted rather than an unmapped status silently vanishing into "UNK".</summary>
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

    private static AeroApiFlightStatusProvider Provider(
        StubHttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        ILogger<AeroApiFlightStatusProvider>? logger = null,
        AeroApiOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/aeroapi/") };
        return new AeroApiFlightStatusProvider(
            http,
            Options.Create(options ?? new AeroApiOptions { ApiKey = "dummy" }),
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<AeroApiFlightStatusProvider>.Instance);
    }

    // ------------------------------------------------------------------
    // ProviderName
    // ------------------------------------------------------------------

    [Fact]
    public void ProviderName_IsAeroApi()
    {
        var provider = Provider(new StubHttpMessageHandler("{}"));

        Assert.Equal("AeroAPI", provider.ProviderName);
    }

    /// <summary>
    /// Documents the consequence of a flight-status provider rename (issue #280 switched the
    /// provider that feeds this data, changing <c>ProviderName</c> in the process):
    /// <c>FlightStatusEvent.ComputeDedupHash</c> folds <c>source</c> (== <c>ProviderName</c>) into
    /// the stored hash, so a hash computed under the prior provider's name never matches one
    /// computed under the current one for the same logical event, even with every other input
    /// identical. Existing stored events are not migrated on a rename, so after such a deploy, the
    /// first sync of each already-tracked flight is expected to record one duplicate
    /// <see cref="FlightStatusEvent"/> before dedup catches up under the new name. This is
    /// accepted, not a bug to fix here.
    /// </summary>
    [Fact]
    public void ComputeDedupHash_DiffersAcrossAProviderRename_ForAnOtherwiseIdenticalEvent()
    {
        var effectiveAt = new DateTime(2026, 8, 30, 6, 35, 0, DateTimeKind.Utc);

        var priorProviderHash = FlightStatusEvent.ComputeDedupHash(
            flightId: 1, statusId: 2, effectiveAt: effectiveAt, source: "PriorProvider", externalId: "TK1028");
        var currentProviderHash = FlightStatusEvent.ComputeDedupHash(
            flightId: 1, statusId: 2, effectiveAt: effectiveAt, source: "AeroAPI", externalId: "TK1028");

        Assert.NotEqual(priorProviderHash, currentProviderHash);
    }

    // ------------------------------------------------------------------
    // Field mapping (recorded fixture)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatusAsync_MapsAllFields_ForTheMatchingServiceDate()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        // 2026-08-30 row: status "Arrived / Gate Arrival", cancelled=false, actual_out and
        // actual_on set, actual_in null, gate/terminal/baggage all populated.
        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.NotNull(result);
        Assert.Equal("ARR", result!.StatusCode);
        Assert.Equal("B9", result.Gate);
        Assert.Equal("2", result.Terminal);
        Assert.Equal("23A", result.BaggageClaim);
        Assert.Equal("A321", result.EquipmentIcao);
        Assert.Equal("THY1028-1787897525-airline-1321p", result.ExternalId);

        Assert.Equal(new DateTime(2026, 8, 30, 6, 35, 0, DateTimeKind.Utc), result.EstimatedDepartureUtc);
        Assert.Equal(new DateTime(2026, 8, 30, 7, 45, 0, DateTimeKind.Utc), result.EstimatedArrivalUtc);
        Assert.Equal(new DateTime(2026, 8, 30, 6, 43, 0, DateTimeKind.Utc), result.ActualDepartureUtc);
        Assert.Equal(new DateTime(2026, 8, 30, 7, 45, 0, DateTimeKind.Utc), result.ActualArrivalUtc); // actual_on

        // EffectiveAtUtc prefers actual_out over estimated/scheduled.
        Assert.Equal(result.ActualDepartureUtc, result.EffectiveAtUtc);

        Assert.NotNull(result.RawPayloadJson);
        Assert.Contains("\"ident_iata\":\"TK1028\"", result.RawPayloadJson);
    }

    [Fact]
    public async Task GetStatusAsync_MapsActualArrival_ToTheRunwayArrival_WhenNoGateArrivalIsReported()
    {
        // Every arrived row in the recording has actual_on set and actual_in null. Without the
        // fallback an arrived flight never gets an actual arrival, so it never stops syncing (#345).
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 29), StatusCallKind.Interactive);

        Assert.Equal(new DateTime(2026, 8, 29, 8, 5, 35, DateTimeKind.Utc), result!.ActualArrivalUtc);
    }

    [Fact]
    public async Task GetStatusAsync_MapsActualArrival_ToTheGateArrival_WhenBothAreReported()
    {
        // The recorded 2026-08-29 row (actual_on 08:05:35Z), with a gate arrival added.
        var body = System.Text.Json.Nodes.JsonNode.Parse(ReadFixture("flight-status.json"))!;
        var row = body["flights"]!.AsArray()
            .Single(f => (string?)f!["scheduled_out"] == "2026-08-29T06:35:00Z")!;
        row["actual_in"] = "2026-08-29T08:12:00Z";
        var handler = new StubHttpMessageHandler(body.ToJsonString());

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 29), StatusCallKind.Interactive);

        Assert.Equal(new DateTime(2026, 8, 29, 8, 12, 0, DateTimeKind.Utc), result!.ActualArrivalUtc);
    }

    [Fact]
    public async Task GetStatusAsync_ParsesTimestampsAsUtc_NotHostLocalTime()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 9, 1), StatusCallKind.Interactive);

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.EstimatedDepartureUtc!.Value.Kind);
        Assert.Equal(new DateTime(2026, 9, 1, 6, 35, 0, DateTimeKind.Utc), result.EstimatedDepartureUtc);
    }

    [Fact]
    public async Task GetStatusAsync_RequestsTheIdentPath()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        await Provider(handler).GetStatusAsync("tk1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal("/aeroapi/flights/TK1028", handler.LastRequestUri!.AbsolutePath);
    }

    // ------------------------------------------------------------------
    // Explicit start/end (issue #283) — deterministic rather than relying on
    // AeroAPI's undocumented default window.
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatusAsync_SendsStartAndEndQueryParameters_DerivedFromTheConfiguredWindowAndNow()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));
        // Deliberately not the option defaults (10 and 2): with the defaults, a BuildUrl that
        // hardcoded the literals instead of reading the options would pass this test too (#308).
        var options = new AeroApiOptions
        {
            ApiKey = "dummy",
            StatusWindowPastDays = 3,
            StatusWindowFutureDays = 1
        };

        await Provider(handler, timeProvider: clock, options: options)
            .GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        var expectedStart = Uri.EscapeDataString("2026-08-27T12:00:00Z");
        var expectedEnd = Uri.EscapeDataString("2026-08-31T12:00:00Z");
        Assert.Equal($"?start={expectedStart}&end={expectedEnd}", handler.LastRequestUri!.Query);
    }

    [Fact]
    public async Task GetStatusAsync_StartAndEndQueryParameters_MoveWithTheClock()
    {
        // Same configured window, different "now" — the request window must move with it rather
        // than being fixed at construction time.
        var clock = new FakeTimeProvider(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));
        // Non-default window, for the same reason as the test above (#308).
        var options = new AeroApiOptions { ApiKey = "dummy", StatusWindowPastDays = 7, StatusWindowFutureDays = 1 };

        await Provider(handler, timeProvider: clock, options: options)
            .GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        var expectedStart = Uri.EscapeDataString("2026-12-25T00:00:00Z");
        var expectedEnd = Uri.EscapeDataString("2027-01-02T00:00:00Z");
        Assert.Equal($"?start={expectedStart}&end={expectedEnd}", handler.LastRequestUri!.Query);
    }

    // ------------------------------------------------------------------
    // No match
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_WhenNoRowMatchesTheServiceDate()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        // The fixture spans 2026-08-19 .. 2026-09-01; a date well outside that range never matches.
        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2027, 1, 1), StatusCallKind.Interactive);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_WhenFlightsArrayIsEmpty()
    {
        var handler = new StubHttpMessageHandler("""{ "flights": [], "links": null, "num_pages": 1 }""");

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Service-date matching is origin-LOCAL, not UTC
    // ------------------------------------------------------------------

    /// <summary>
    /// Two operations of the same ident a day apart, each carrying its own <c>origin.timezone</c>
    /// exactly as AeroAPI supplies it. <paramref name="timezone"/> is deliberately a parameter: the
    /// whole defect is invisible at SOF (UTC+3, 06:35Z departure), where the origin-local and UTC
    /// calendar dates are the same date — which is why every pre-existing test in this file used it.
    /// </summary>
    private static string TwoDayBody(string originIata, string? timezone, params string[] scheduledOutUtc)
    {
        var timezoneJson = timezone is null ? "null" : $"\"{timezone}\"";
        var rows = scheduledOutUtc.Select(t => $$"""
            {
              "ident_iata": "NZ101",
              "fa_flight_id": "ANZ101-{{t}}",
              "cancelled": false,
              "diverted": false,
              "origin": { "code_iata": "{{originIata}}", "timezone": {{timezoneJson}} },
              "scheduled_out": "{{t}}",
              "scheduled_in": "{{t}}",
              "status": "Scheduled"
            }
            """);

        return $$"""{ "flights": [{{string.Join(",", rows)}}], "links": null, "num_pages": 1 }""";
    }

    [Fact]
    public async Task GetStatusAsync_MatchesTheOriginsLocalServiceDate_ForAFarEastAirport()
    {
        // AKL is UTC+13 on this date. NZ101 departing 2026-09-29 07:00 local IS 2026-09-28T18:00Z,
        // and the NEXT day's operation is 2026-09-29T18:00Z. AeroApiFlightSearchProvider reports
        // ServiceDate as the origin's LOCAL day (that is the entire point of its window shift),
        // TrackFlightCommandHandler stores it verbatim and SyncFlightStatusCommand hands it back
        // here — so matching on the UTC calendar date silently selected the following day's
        // operation and applied its gate, terminal and status to the tracked flight.
        var handler = new StubHttpMessageHandler(
            TwoDayBody("AKL", "Pacific/Auckland", "2026-09-28T18:00:00Z", "2026-09-29T18:00:00Z"));

        var result = await Provider(handler).GetStatusAsync("NZ101", new DateOnly(2026, 9, 29), StatusCallKind.Interactive);

        Assert.NotNull(result);
        Assert.Equal("ANZ101-2026-09-28T18:00:00Z", result!.ExternalId);
    }

    [Fact]
    public async Task GetStatusAsync_MatchesTheOriginsLocalServiceDate_ForAFarWestAirport()
    {
        // The mirror image: LAX is UTC−7 in September, so a 2026-09-30T04:00Z departure is local
        // 2026-09-29 21:00, and 2026-09-29T04:00Z is local 2026-09-28. A UTC comparison picks the
        // PREVIOUS day's operation here — the error runs in both directions.
        var handler = new StubHttpMessageHandler(
            TwoDayBody("LAX", "America/Los_Angeles", "2026-09-29T04:00:00Z", "2026-09-30T04:00:00Z"));

        var result = await Provider(handler).GetStatusAsync("NZ101", new DateOnly(2026, 9, 29), StatusCallKind.Interactive);

        Assert.NotNull(result);
        Assert.Equal("ANZ101-2026-09-30T04:00:00Z", result!.ExternalId);
    }

    [Fact]
    public async Task GetStatusAsync_FallsBackToTheUtcDate_WhenTheRowCarriesNoOriginTimezone()
    {
        // Degrading to the previous behaviour beats matching nothing: an approximate row is more
        // useful to a traveller than no status at all, and it is correct for most of the world.
        var handler = new StubHttpMessageHandler(
            TwoDayBody("AKL", null, "2026-09-28T18:00:00Z", "2026-09-29T18:00:00Z"));

        var result = await Provider(handler).GetStatusAsync("NZ101", new DateOnly(2026, 9, 29), StatusCallKind.Interactive);

        Assert.NotNull(result);
        Assert.Equal("ANZ101-2026-09-29T18:00:00Z", result!.ExternalId);
    }

    [Fact]
    public async Task GetStatusAsync_FallsBackToTheUtcDate_WhenTheOriginTimezoneIsUnloadable()
    {
        var handler = new StubHttpMessageHandler(
            TwoDayBody("AKL", "Not/AZone", "2026-09-28T18:00:00Z", "2026-09-29T18:00:00Z"));

        var result = await Provider(handler).GetStatusAsync("NZ101", new DateOnly(2026, 9, 29), StatusCallKind.Interactive);

        Assert.NotNull(result);
        Assert.Equal("ANZ101-2026-09-29T18:00:00Z", result!.ExternalId);
    }

    // ------------------------------------------------------------------
    // Status-code normalisation
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatusAsync_MapsScheduled_ToSch()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 9, 1), StatusCallKind.Interactive);

        Assert.Equal("SCH", result!.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_MapsArrivedGateArrival_ToArr()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 25), StatusCallKind.Interactive);

        Assert.Equal("ARR", result!.StatusCode);
    }

    [Theory]
    [InlineData("En Route", "DEP")]
    [InlineData("En Route / On Time", "DEP")]
    [InlineData("Departed", "DEP")]
    [InlineData("Taxiing / Left Gate", "DEP")]
    [InlineData("Landed", "ARR")]
    [InlineData("Diverted", "DIV")]
    [InlineData("Cancelled", "CAN")]
    public async Task GetStatusAsync_MapsKnownStatusProse_ToTheDocumentedCode(string statusText, string expectedCode)
    {
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: statusText));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal(expectedCode, result!.StatusCode);
    }

    [Theory]
    [InlineData("Scheduled / Delayed", "2026-08-30T07:15:00Z", "DLY")] // 40 min late
    [InlineData("Scheduled", "2026-08-30T06:55:00Z", "DLY")]           // 20 min late: the times say delayed
    [InlineData("Delayed", "2026-08-30T06:50:00Z", "DLY")]             // exactly the 15 min threshold
    [InlineData("Scheduled", "2026-08-30T06:49:00Z", "SCH")]           // 14 min: under the threshold
    [InlineData("Scheduled / Delayed", "2026-08-30T06:45:00Z", "SCH")] // 10 min: the times win over the text
    [InlineData("Delayed", "2026-08-30T06:35:00Z", "SCH")]             // on time
    [InlineData("En Route / Delayed", "2026-08-30T07:15:00Z", "DEP")]  // departed: the times don't apply
    [InlineData("Taxiing / Left Gate", "2026-08-30T07:15:00Z", "DEP")]
    public async Task GetStatusAsync_BeforeDeparture_ADelayComesFromTheTimes(
        string statusText, string estimatedOut, string expectedCode)
    {
        // Scheduled out 06:35 (#352).
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: statusText, estimatedOut: estimatedOut));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal(expectedCode, result!.StatusCode);
    }

    [Theory]
    [InlineData("Scheduled / Delayed", "DLY")]
    [InlineData("Delayed", "DLY")]
    [InlineData("Scheduled", "SCH")]
    public async Task GetStatusAsync_WithoutAnEstimatedDeparture_TheTextDecides(string statusText, string expectedCode)
    {
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: statusText, estimatedOut: null));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal(expectedCode, result!.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsTheScheduledDeparture()
    {
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: "Scheduled"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal(new DateTime(2026, 8, 30, 6, 35, 0, DateTimeKind.Utc), result!.ScheduledDepartureUtc);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsTheScheduledArrival()
    {
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: "Scheduled"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal(new DateTime(2026, 8, 30, 8, 5, 0, DateTimeKind.Utc), result!.ScheduledArrivalUtc);
    }

    [Fact]
    public void ProviderName_IsTheSharedConstant_WithItsStoredValue()
    {
        // Recorded in flight_status_events.Source and hashed into every stored dedup hash (#351). Changing
        // the string — including "correcting" it to the "AeroApi" spelling used elsewhere — would stop every
        // stored hash matching, so each tracked flight would re-record and re-push its current status.
        // Phase 2's alert path must pass this same constant (#386).
        Assert.Equal("AeroAPI", FlightStatusSources.AeroApi);
        Assert.Equal(FlightStatusSources.AeroApi, Provider(new StubHttpMessageHandler("{}")).ProviderName);
    }

    [Fact]
    public async Task GetStatusAsync_CancelledFlag_WinsOverStatusText_RegardlessOfStatusString()
    {
        // "Scheduled" text but cancelled=true — the boolean is authoritative.
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: "Scheduled", cancelled: true));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal("CAN", result!.StatusCode);
    }

    [Theory]
    [InlineData("En Route")]
    [InlineData("Scheduled")]
    [InlineData("En Route / On Time")]
    public async Task GetStatusAsync_DivertedFlag_WinsOverStatusText_RegardlessOfStatusString(string statusText)
    {
        // AeroAPI supplies `diverted` as a boolean alongside `cancelled`, and for the same reason:
        // the prose `status` can lag reality. The cancelled check already guarded against that;
        // `diverted` was read by nobody, so a genuinely diverted flight reported as "En Route"
        // mapped to DEP — the exact failure the cancelled check exists to prevent.
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: statusText, diverted: true));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal("DIV", result!.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_CancelledWins_WhenBothFlagsAreSet()
    {
        // A flight cannot really be both; if the payload claims it is, cancellation is the more
        // consequential outcome for a traveller and is documented to win.
        var handler = new StubHttpMessageHandler(
            SingleFlightBody(status: "En Route", cancelled: true, diverted: true));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal("CAN", result!.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_UnrecognisedStatus_MapsToUnk_AndLogsAWarning_RatherThanFailingSilently()
    {
        var logger = new CapturingLogger<AeroApiFlightStatusProvider>();
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: "Some New Status FlightAware Invents Later"));

        var result = await Provider(handler, logger: logger).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal("UNK", result!.StatusCode);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Some New Status FlightAware Invents Later"));
    }

    [Fact]
    public async Task GetStatusAsync_NullStatus_MapsToUnk_AndLogsAWarning()
    {
        var logger = new CapturingLogger<AeroApiFlightStatusProvider>();
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: null));

        var result = await Provider(handler, logger: logger).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal("UNK", result!.StatusCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ------------------------------------------------------------------
    // Retry intent (#375)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatusAsync_MarksAScheduledPoll_SoTheClientDoesNotRetryIt()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.ScheduledPoll);

        Assert.True(handler.LastRequestSkippedRetries);
    }

    [Fact]
    public async Task GetStatusAsync_LeavesAnInteractiveCall_Retryable()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.False(handler.LastRequestSkippedRetries);
    }

    private static string SingleFlightBody(string? status, bool cancelled = false, bool diverted = false,
        string? estimatedOut = "2026-08-30T06:35:00Z")
    {
        var statusJson = status is null ? "null" : $"\"{status}\"";
        return $$"""
        {
          "flights": [
            {
              "ident_iata": "TK1028",
              "fa_flight_id": "THY1028-test",
              "cancelled": {{(cancelled ? "true" : "false")}},
              "diverted": {{(diverted ? "true" : "false")}},
              "scheduled_out": "2026-08-30T06:35:00Z",
              "estimated_out": {{(estimatedOut is null ? "null" : $"\"{estimatedOut}\"")}},
              "actual_out": null,
              "scheduled_in": "2026-08-30T08:05:00Z",
              "estimated_in": "2026-08-30T08:05:00Z",
              "actual_in": null,
              "status": {{statusJson}},
              "aircraft_type": "A321",
              "gate_origin": "B9",
              "terminal_origin": "2",
              "baggage_claim": null
            }
          ],
          "links": null,
          "num_pages": 1
        }
        """;
    }
}
