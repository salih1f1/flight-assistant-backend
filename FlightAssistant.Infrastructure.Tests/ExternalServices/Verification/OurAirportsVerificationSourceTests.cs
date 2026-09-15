using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// Drives <see cref="OurAirportsVerificationSource"/> against a small recorded slice of the real
/// OurAirports CSV (<c>FlightAssistant.Infrastructure.Tests/Fixtures/Verification/ourairports-slice.csv</c>,
/// header kept verbatim) via a fake <see cref="HttpMessageHandler"/> -- #367 task 3. No live network
/// call is made by any test in this file.
/// </summary>
public class OurAirportsVerificationSourceTests
{
    private static readonly string FixtureCsv =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Verification", "ourairports-slice.csv"));

    /// <summary>Serves a fixed status/body for every request and counts how many were made.</summary>
    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/csv")
            });
        }
    }

    /// <summary>Never completes on its own -- only when the owning <see cref="HttpClient"/>'s
    /// <see cref="HttpClient.Timeout"/> cancels the token it was sent, simulating a real network
    /// timeout rather than the caller's own cancellation.</summary>
    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable — the delay above never completes normally.");
        }
    }

    private static OurAirportsVerificationSource Source(HttpMessageHandler handler, TimeProvider? timeProvider = null, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        return new OurAirportsVerificationSource(http, timeProvider ?? TimeProvider.System, NullLogger<OurAirportsVerificationSource>.Instance);
    }

    // ── Mapping ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LookupAsync_MapsAllEightColumns_ForAMatchingIataRow()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.False(result.IsError);
        Assert.True(result.Lookup!.IsFound);
        var values = result.Lookup.Values!;
        Assert.Equal("LHR", values["Iata"]);
        Assert.Equal("EGLL", values["Icao"]);
        Assert.Equal("London Heathrow Airport", values["Name"]);
        Assert.Equal("London", values["City"]);
        Assert.Equal("GB", values["CountryCode"]);
        Assert.Equal("51.4706", values["Latitude"]);
        Assert.Equal("-0.461941", values["Longitude"]);
        Assert.Equal("83", values["ElevationFt"]);
    }

    [Fact]
    public async Task LookupAsync_FallsBackToIcao_WhenIataIsMissing()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        // The "Icao Only Field" row carries no iata_code at all.
        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport(null, "EGZZ"));

        Assert.True(result.Lookup!.IsFound);
        Assert.Equal("Icao Only Field", result.Lookup.Values!["Name"]);
    }

    [Fact]
    public async Task LookupAsync_FallsBackToIcao_WhenIataHasNoMatch()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("NOPE", "EGZZ"));

        Assert.True(result.Lookup!.IsFound);
        Assert.Equal("Icao Only Field", result.Lookup.Values!["Name"]);
    }

    [Fact]
    public async Task LookupAsync_IgnoresClosedRows()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        // ZZZ only appears on the "closed" row in the fixture.
        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("ZZZ", "ZZZZ"));

        Assert.False(result.IsError);
        Assert.False(result.Lookup!.IsFound);
    }

    [Fact]
    public async Task LookupAsync_PicksTheHighestScore_AmongDuplicateIataRows()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("DUP", null));

        Assert.True(result.Lookup!.IsFound);
        Assert.Equal("Dup High", result.Lookup.Values!["Name"]);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNotFound_WhenNothingMatches()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("XXX", null));

        Assert.False(result.IsError);
        Assert.False(result.Lookup!.IsFound);
    }

    [Fact]
    public async Task LookupAsync_MapsAnEmptyElevation_ToAnEmptyValue_NotAThrow()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("ELV", null));

        Assert.True(result.Lookup!.IsFound);
        Assert.True(string.IsNullOrEmpty(result.Lookup.Values!["ElevationFt"]));
    }

    // ── Source errors ───────────────────────────────────────────────────────

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnTimeout()
    {
        var source = Source(new NeverRespondingHandler(), timeout: TimeSpan.FromMilliseconds(20));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_On500()
    {
        var source = Source(new StaticHandler(HttpStatusCode.InternalServerError, "boom"));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_On429_NeverNotFound()
    {
        // Distinguishing break-proof #4: mapping 429 to NotFound instead of a source error would
        // make a throttled response look exactly like "no data", and this test would pass either
        // way if it only asserted IsError -- so it pins IsFound as well.
        var source = Source(new StaticHandler(HttpStatusCode.TooManyRequests, "too many requests"));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_On404_NeverNotFound()
    {
        var source = Source(new StaticHandler(HttpStatusCode.NotFound, "not found"));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnGarbageContent()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, "this is not a csv file at all"));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(result.IsError);
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    [Fact]
    public void Constructing_MakesNoHttpRequest()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, FixtureCsv);

        _ = Source(handler);

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_SecondCallWithinTwentyFourHours_MakesNoSecondHttpRequest()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, FixtureCsv);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        clock.Advance(TimeSpan.FromHours(23));
        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_AfterTwentyFourHours_DownloadsAgain()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, FixtureCsv);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        clock.Advance(TimeSpan.FromHours(24));
        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ReleaseCache_MakesTheNextLookupDownloadAgain()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, FixtureCsv);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        source.ReleaseCache();
        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_ConcurrentCallsThatBothMissTheCache_MakeOnlyOneHttpRequest()
    {
        var gate = new SemaphoreSlim(0);
        var callCount = 0;

        var handler = new DelegateHandler(async () =>
        {
            Interlocked.Increment(ref callCount);
            await gate.WaitAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FixtureCsv, Encoding.UTF8, "text/csv") };
        });
        var source = Source(handler);

        var first = source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        var second = source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("DUP", null));

        // Give both calls a chance to reach the (blocked) handler before releasing it.
        await Task.Delay(50);
        gate.Release(2);
        await Task.WhenAll(first, second);

        Assert.Equal(1, callCount);
        Assert.True((await first).Lookup!.IsFound);
        Assert.True((await second).Lookup!.IsFound);
    }

    private sealed class DelegateHandler(Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond();
    }

    // ── Isolation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LookupAsync_RejectsAnUnsupportedEntityType()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("GB")));
    }

    [Fact]
    public void SupportedEntityTypes_IsAirportOnly()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        Assert.Equal(new[] { VerificationEntityType.Airport }, source.SupportedEntityTypes);
    }

    [Fact]
    public void Name_And_CostPerRowUsd()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        Assert.Equal("ourairports", source.Name);
        Assert.Equal(0m, source.CostPerRowUsd);
    }
}
