using System.Net;
using System.Text;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        ILogger<AeroApiFlightStatusProvider>? logger = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/aeroapi/") };
        return new AeroApiFlightStatusProvider(
            http,
            Options.Create(new AeroApiOptions { ApiKey = "dummy" }),
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

        // 2026-08-30 row: status "Arrived / Gate Arrival", cancelled=false, actual_out set,
        // actual_in null (arrival still finalising), gate/terminal/baggage all populated.
        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30));

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
        Assert.Null(result.ActualArrivalUtc);

        // EffectiveAtUtc prefers actual_out over estimated/scheduled.
        Assert.Equal(result.ActualDepartureUtc, result.EffectiveAtUtc);

        Assert.NotNull(result.RawPayloadJson);
        Assert.Contains("\"ident_iata\":\"TK1028\"", result.RawPayloadJson);
    }

    [Fact]
    public async Task GetStatusAsync_ParsesTimestampsAsUtc_NotHostLocalTime()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 9, 1));

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.EstimatedDepartureUtc!.Value.Kind);
        Assert.Equal(new DateTime(2026, 9, 1, 6, 35, 0, DateTimeKind.Utc), result.EstimatedDepartureUtc);
    }

    [Fact]
    public async Task GetStatusAsync_RequestsTheIdentPath()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        await Provider(handler).GetStatusAsync("tk1028", new DateOnly(2026, 8, 30));

        Assert.Equal("/aeroapi/flights/TK1028", handler.LastRequestUri!.AbsolutePath);
    }

    // ------------------------------------------------------------------
    // No match
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_WhenNoRowMatchesTheServiceDate()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        // The fixture spans 2026-08-19 .. 2026-09-01; a date well outside that range never matches.
        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2027, 1, 1));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_WhenFlightsArrayIsEmpty()
    {
        var handler = new StubHttpMessageHandler("""{ "flights": [], "links": null, "num_pages": 1 }""");

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30));

        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Status-code normalisation
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatusAsync_MapsScheduled_ToSch()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 9, 1));

        Assert.Equal("SCH", result!.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_MapsArrivedGateArrival_ToArr()
    {
        var handler = new StubHttpMessageHandler(ReadFixture("flight-status.json"));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 25));

        Assert.Equal("ARR", result!.StatusCode);
    }

    [Theory]
    [InlineData("En Route", "DEP")]
    [InlineData("En Route / On Time", "DEP")]
    [InlineData("Departed", "DEP")]
    [InlineData("Landed", "ARR")]
    [InlineData("Delayed", "DLY")]
    [InlineData("Diverted", "DIV")]
    [InlineData("Cancelled", "CAN")]
    public async Task GetStatusAsync_MapsKnownStatusProse_ToTheDocumentedCode(string statusText, string expectedCode)
    {
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: statusText));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30));

        Assert.Equal(expectedCode, result!.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_CancelledFlag_WinsOverStatusText_RegardlessOfStatusString()
    {
        // "Scheduled" text but cancelled=true — the boolean is authoritative.
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: "Scheduled", cancelled: true));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30));

        Assert.Equal("CAN", result!.StatusCode);
    }

    [Fact]
    public async Task GetStatusAsync_UnrecognisedStatus_MapsToUnk_AndLogsAWarning_RatherThanFailingSilently()
    {
        var logger = new CapturingLogger<AeroApiFlightStatusProvider>();
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: "Some New Status FlightAware Invents Later"));

        var result = await Provider(handler, logger: logger).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30));

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

        var result = await Provider(handler, logger: logger).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30));

        Assert.Equal("UNK", result!.StatusCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    private static string SingleFlightBody(string? status, bool cancelled = false)
    {
        var statusJson = status is null ? "null" : $"\"{status}\"";
        return $$"""
        {
          "flights": [
            {
              "ident_iata": "TK1028",
              "fa_flight_id": "THY1028-test",
              "cancelled": {{(cancelled ? "true" : "false")}},
              "diverted": false,
              "scheduled_out": "2026-08-30T06:35:00Z",
              "estimated_out": "2026-08-30T06:35:00Z",
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
