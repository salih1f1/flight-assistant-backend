using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// Drives <see cref="AeroApiVerificationSource"/> against stubbed AeroAPI responses, including the
/// recorded fixtures under <c>Fixtures/AeroApi/airport-sof.json</c> and <c>operator-baw.json</c>
/// (#401) -- never the real API. Pins R2 (ICAO preferred, IATA fallback, both blank is NotFound with
/// no call sent), R3 (a 404 is NotFound; everything else is a fixed-category source error with no
/// upstream message), R6 (no cached dataset) and R7 (an airport check fills the timezone cache
/// through <see cref="AeroApiAirportTimezoneLookup"/>'s own key).
/// </summary>
public class AeroApiVerificationSourceTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AeroApi");

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(FixturesDir, name));

    private sealed class StubHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    /// <summary>Fails the test if it is ever invoked -- used to prove no request is sent at all.</summary>
    private sealed class NoRequestHttpMessageHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("No call should have been sent to aeroapi.");
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            // The source must reuse the shared "AeroApi" client (auth, resilience, throttle) --
            // never a dedicated client of its own the way the two free dataset sources have one.
            Assert.Equal(AeroApiVerificationSource.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://stub/aeroapi/") };
        }
    }

    private static AeroApiVerificationSource Source(HttpMessageHandler handler, ICacheService? cache = null) =>
        new(new StubHttpClientFactory(handler), cache ?? Substitute.For<ICacheService>(),
            Options.Create(new AeroApiOptions { ApiKey = "dummy-key" }),
            NullLogger<AeroApiVerificationSource>.Instance);

    // ---- Basics -----------------------------------------------------------------------------

    [Fact]
    public void Name_CostPerRowUsd_AndSupportedEntityTypes_AreAsSpecified()
    {
        var source = Source(new NoRequestHttpMessageHandler());

        Assert.Equal("aeroapi", source.Name);
        Assert.Equal(0.015m, source.CostPerRowUsd);
        Assert.Equal(
            new[] { VerificationEntityType.Airport, VerificationEntityType.Airline },
            source.SupportedEntityTypes);
    }

    [Fact]
    public async Task LookupAsync_AnUnsupportedEntityType_Throws_WithoutCallingAnything()
    {
        var handler = new NoRequestHttpMessageHandler();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Source(handler).LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("GB")));

        Assert.Equal(0, handler.Calls);
    }

    // ---- R2: ICAO preferred, IATA fallback ---------------------------------------------------

    [Fact]
    public async Task LookupAsync_Airport_PrefersIcao_WhenBothCodesArePresent()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReadFixture("airport-sof.json"));

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"));

        Assert.True(result.Lookup!.IsFound);
        Assert.EndsWith("airports/LBSF", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupAsync_Airport_FallsBackToIata_WhenIcaoIsBlank()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReadFixture("airport-sof.json"));

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", null));

        Assert.True(result.Lookup!.IsFound);
        Assert.EndsWith("airports/SOF", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupAsync_Airline_PrefersIcao_WhenBothCodesArePresent()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReadFixture("operator-baw.json"));

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airline, VerificationSourceLookupKey.ForAirline("BA", "BAW"));

        Assert.True(result.Lookup!.IsFound);
        Assert.EndsWith("operators/BAW", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupAsync_Airline_FallsBackToIata_WhenIcaoIsBlank()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReadFixture("operator-baw.json"));

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airline, VerificationSourceLookupKey.ForAirline("BA", " "));

        Assert.True(result.Lookup!.IsFound);
        Assert.EndsWith("operators/BA", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", "  ")]
    public async Task LookupAsync_BothCodesBlank_IsNotFound_WithNoCallSent(string? iata, string? icao)
    {
        var handler = new NoRequestHttpMessageHandler();

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport(iata, icao));

        Assert.False(result.IsError);
        Assert.False(result.Lookup!.IsFound);
        Assert.Equal(0, handler.Calls);
    }

    // ---- R3: a 404 is NotFound; everything else is a fixed-category source error --------------

    [Fact]
    public async Task LookupAsync_A404_IsNotFound()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, """{ "title": "not found" }""");

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("ZZZ", "ZZZZ"));

        Assert.False(result.IsError);
        Assert.False(result.Lookup!.IsFound);
    }

    [Fact]
    public async Task LookupAsync_A500_IsASourceError_WithNoUpstreamMessage()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.InternalServerError, """{ "title": "boom", "detail": "db-prod.internal:5432 exploded" }""");

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"));

        Assert.True(result.IsError);
        Assert.Equal(AeroApiVerificationSource.HttpStatusError(HttpStatusCode.InternalServerError), result.Error);
        Assert.DoesNotContain("db-prod", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupAsync_A429_IsASourceError_WithNoUpstreamMessage()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, """{ "title": "too many requests" }""");

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"));

        Assert.True(result.IsError);
        Assert.Equal(AeroApiVerificationSource.HttpStatusError(HttpStatusCode.TooManyRequests), result.Error);
    }

    [Fact]
    public async Task LookupAsync_UnparseableContent_IsASourceError_WithNoUpstreamMessage()
    {
        // 200 OK with a body that is not valid JSON -- AeroApiResponse.ReadOrThrowAsync's own parse
        // failure, distinct from a non-success HTTP status.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "not json");

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"));

        Assert.True(result.IsError);
        Assert.Equal(AeroApiVerificationSource.ParseError, result.Error);
    }

    [Fact]
    public async Task LookupAsync_ATimeout_IsASourceError_WithNoUpstreamMessage()
    {
        // Simulates the resilience pipeline's own attempt/total timeout firing (not the caller's own
        // ct): a TaskCanceledException while ct itself was never cancelled.
        var handler = new ThrowingHttpMessageHandler(new TaskCanceledException("attempt timeout"));

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"));

        Assert.True(result.IsError);
        Assert.Equal(AeroApiVerificationSource.TimedOutError, result.Error);
    }

    [Fact]
    public async Task LookupAsync_Unreachable_IsASourceError_WithNoUpstreamMessage()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"));

        Assert.True(result.IsError);
        Assert.Equal(AeroApiVerificationSource.NetworkError, result.Error);
    }

    [Fact]
    public async Task LookupAsync_TheCallersOwnCancellation_Propagates_RatherThanBecomingASourceError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new ThrowingHttpMessageHandler(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Source(handler).LookupAsync(
                VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"), cts.Token));
    }

    // ---- Mapping ------------------------------------------------------------------------------

    [Fact]
    public async Task LookupAsync_Airport_MapsEveryCheckedColumn_FromTheRecordedFixture()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReadFixture("airport-sof.json"));

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"));

        var values = result.Lookup!.Values!;
        Assert.Equal(
            VerificationCheckedColumns.GetColumns(VerificationEntityType.Airport).Select(c => c.Name).ToHashSet(),
            values.Keys.ToHashSet());
        Assert.Equal("SOF", values[nameof(Airport.Iata)]);
        Assert.Equal("LBSF", values[nameof(Airport.Icao)]);
        Assert.Equal("Sofia Airport", values[nameof(Airport.Name)]);
        Assert.Equal("Sofia", values[nameof(Airport.City)]);
        Assert.Equal("BG", values[nameof(Airport.CountryCode)]);
        Assert.Equal("42.695194", values[nameof(Airport.Latitude)]);
        Assert.Equal("23.406167", values[nameof(Airport.Longitude)]);
        // Feet, unconverted -- AeroAPI's `elevation` is already feet.
        Assert.Equal("1742", values[nameof(Airport.ElevationFt)]);
    }

    [Fact]
    public async Task LookupAsync_Airline_MapsExactlyIataIcaoName()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReadFixture("operator-baw.json"));

        var result = await Source(handler).LookupAsync(
            VerificationEntityType.Airline, VerificationSourceLookupKey.ForAirline("BA", "BAW"));

        var values = result.Lookup!.Values!;
        Assert.Equal(new HashSet<string> { "Iata", "Icao", "Name" }, values.Keys.ToHashSet());
        Assert.Equal("BA", values[nameof(Airline.Iata)]);
        Assert.Equal("BAW", values[nameof(Airline.Icao)]);
        Assert.Equal("British Airways", values[nameof(Airline.Name)]);
    }

    // ---- R7: an airport check fills the timezone cache -----------------------------------------

    [Fact]
    public async Task LookupAsync_Airport_FillsTheTimezoneCache_ThroughTheSharedKeyAndDuration()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReadFixture("airport-sof.json"));
        var cache = Substitute.For<ICacheService>();

        await Source(handler, cache).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SOF", "LBSF"));

        await cache.Received(1).SetAsync(
            AeroApiAirportTimezoneLookup.CacheKeyFor("SOF"),
            "Europe/Sofia",
            AeroApiAirportTimezoneLookup.SuccessCacheDuration,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LookupAsync_Airline_NeverWritesTheTimezoneCache()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReadFixture("operator-baw.json"));
        var cache = Substitute.For<ICacheService>();

        await Source(handler, cache).LookupAsync(
            VerificationEntityType.Airline, VerificationSourceLookupKey.ForAirline("BA", "BAW"));

        await cache.DidNotReceiveWithAnyArgs().SetAsync<string>(default!, default!, default, default);
    }

    [Fact]
    public async Task LookupAsync_A404_NeverWritesTheTimezoneCache()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, """{ "title": "not found" }""");
        var cache = Substitute.For<ICacheService>();

        await Source(handler, cache).LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("ZZZ", "ZZZZ"));

        await cache.DidNotReceiveWithAnyArgs().SetAsync<string>(default!, default!, default, default);
    }

    // ---- R6: no cached dataset ------------------------------------------------------------------

    [Fact]
    public void HasCachedData_IsAlwaysFalse()
    {
        Assert.False(Source(new NoRequestHttpMessageHandler()).HasCachedData);
    }

    [Fact]
    public void ReleaseCache_IsANoOp()
    {
        var source = Source(new NoRequestHttpMessageHandler());

        var exception = Record.Exception(() => source.ReleaseCache(TimeSpan.Zero));

        Assert.Null(exception);
        Assert.False(source.HasCachedData);
    }
}
