using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroDataBox;

/// <summary>
/// Drives <see cref="AeroDataBoxFlightSearchProvider"/> against a recorded AeroDataBox
/// <c>/flights/airports/iata/...</c> payload (see
/// <c>FlightAssistant.Infrastructure.Tests/Fixtures/AeroDataBox/departures.json</c>), mirroring
/// <c>AeroApiFlightSearchProviderTests</c>'s scripted-handler-plus-fixture approach. No live call
/// is ever made and the configured key here is always a dummy value that must never appear in any
/// assertion or log output.
/// </summary>
public class AeroDataBoxFlightSearchProviderTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AeroDataBox");

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixturesDir, name));

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
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            if (_headers is not null)
            {
                foreach (var (key, value) in _headers)
                    response.Headers.TryAddWithoutValidation(key, value);
            }
            return Task.FromResult(response);
        }
    }

    /// <summary>Throws on every request, to simulate the host being unreachable.</summary>
    private sealed class UnreachableHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated connection failure");
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

    // ------------------------------------------------------------------
    // The request URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_BuildsTheUrl_WithTheWholeLocalDayWindow_AndWithCodesharedTrue()
    {
        var (provider, handler) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        await provider.SearchAsync(SofToIst with { IncludeCodeshares = true });

        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.Equal("/flights/airports/iata/SOF/2026-09-29T00:00/2026-09-29T23:59", request.AbsolutePath);
        Assert.Contains("withLeg=true", request.Query);
        Assert.Contains("direction=Departure", request.Query);
        Assert.Contains("withCancelled=false", request.Query);
        Assert.Contains("withCargo=false", request.Query);
        Assert.Contains("withPrivate=false", request.Query);
        Assert.Contains("withCodeshared=true", request.Query);
    }

    [Fact]
    public async Task SearchAsync_BuildsTheUrl_WithCodesharedFalse()
    {
        var (provider, handler) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        await provider.SearchAsync(SofToIst with { IncludeCodeshares = false });

        Assert.Single(handler.Requests);
        Assert.Contains("withCodeshared=false", handler.Requests[0].Query);
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
    // The revised-time window gotcha (Step 2 of the brief)
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
    public async Task SearchAsync_ExcludesARowRevisedOntoADifferentCalendarDay()
    {
        // TK 1032: scheduled 23:50 local on the 29th, revised to 00:20 local on the 30th — a
        // different day from the requested 2026-09-29, so it is dropped even though it is present
        // in the (whole-local-day) response.
        var (provider, _) = Provider(HttpStatusCode.OK, ReadFixture("departures.json"));

        var results = await provider.SearchAsync(SofToIst);

        Assert.DoesNotContain(results, r => r.FlightNumber == "TK1032");
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
    public async Task SearchAsync_MapsAnyOtherFailureStatus_ToBadGateway()
    {
        var (provider, _) = Provider(HttpStatusCode.InternalServerError, """{ "message": "boom" }""");

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

    // ------------------------------------------------------------------
    // Guard: a route search needs both airports
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, "IST")]
    [InlineData("SOF", null)]
    public async Task SearchAsync_RejectsASearchMissingEitherAirport(string? departureIata, string? arrivalIata)
    {
        var (provider, handler) = Provider(HttpStatusCode.OK, """{ "departures": [] }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => provider.SearchAsync(
            new FlightSearchCriteria(departureIata, arrivalIata, new DateOnly(2026, 9, 29), null)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Empty(handler.Requests);
    }
}
