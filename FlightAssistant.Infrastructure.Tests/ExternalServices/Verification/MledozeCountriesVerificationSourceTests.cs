using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// Drives <see cref="MledozeCountriesVerificationSource"/> against a small recorded slice of the
/// real mledoze/countries <c>countries.json</c> (<c>FlightAssistant.Infrastructure.Tests/Fixtures/Verification/countries-slice.json</c>,
/// which keeps the real nested <c>name.common</c>/<c>name.official</c> record shape) via a fake
/// <see cref="HttpMessageHandler"/> -- #367 task 3. No live network call is made by any test in
/// this file.
/// </summary>
public class MledozeCountriesVerificationSourceTests
{
    private static readonly string FixtureJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Verification", "countries-slice.json"));

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable — the delay above never completes normally.");
        }
    }

    private static MledozeCountriesVerificationSource Source(HttpMessageHandler handler, TimeProvider? timeProvider = null, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        return new MledozeCountriesVerificationSource(http, timeProvider ?? TimeProvider.System, NullLogger<MledozeCountriesVerificationSource>.Instance);
    }

    // ── Mapping ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LookupAsync_MapsAllSixColumns_UsingNameCommon_NotOfficial()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureJson));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.Lookup!.IsFound);
        var values = result.Lookup.Values!;
        Assert.Equal("TR", values["Code"]);
        Assert.Equal("Turkey", values["Name"]); // name.common, not "Republic of Türkiye" (official)
        Assert.Equal("TUR", values["Alpha3"]);
        Assert.Equal("792", values["NumericCode"]);
        Assert.Equal("Asia", values["Region"]);
        Assert.Equal("Western Asia", values["Subregion"]);
    }

    [Fact]
    public async Task LookupAsync_IsCaseInsensitive_OnTheCountryCode()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureJson));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("ci"));

        Assert.True(result.Lookup!.IsFound);
        Assert.Equal("Ivory Coast", result.Lookup.Values!["Name"]);
    }

    [Fact]
    public async Task LookupAsync_MapsAnEmptySubregion_ToAnEmptyValue_NotAThrow()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureJson));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("AQ"));

        Assert.True(result.Lookup!.IsFound);
        Assert.True(string.IsNullOrEmpty(result.Lookup.Values!["Subregion"]));
    }

    [Fact]
    public async Task LookupAsync_ReturnsNotFound_WhenNothingMatches()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureJson));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("ZZ"));

        Assert.False(result.IsError);
        Assert.False(result.Lookup!.IsFound);
    }

    // ── Source errors ───────────────────────────────────────────────────────

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnTimeout()
    {
        var source = Source(new NeverRespondingHandler(), timeout: TimeSpan.FromMilliseconds(20));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_On500()
    {
        var source = Source(new StaticHandler(HttpStatusCode.InternalServerError, "boom"));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_On429_NeverNotFound()
    {
        var source = Source(new StaticHandler(HttpStatusCode.TooManyRequests, "too many requests"));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnGarbageContent()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, "not json at all {{{"));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    [Fact]
    public void Constructing_MakesNoHttpRequest()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, FixtureJson);

        _ = Source(handler);

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_SecondCallWithinTwentyFourHours_MakesNoSecondHttpRequest()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, FixtureJson);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));
        clock.Advance(TimeSpan.FromHours(23));
        await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("CI"));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_AfterTwentyFourHours_DownloadsAgain()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, FixtureJson);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));
        clock.Advance(TimeSpan.FromHours(24));
        await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ReleaseCache_MakesTheNextLookupDownloadAgain()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, FixtureJson);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));
        source.ReleaseCache();
        await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.Equal(2, handler.CallCount);
    }

    // ── Isolation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LookupAsync_RejectsAnUnsupportedEntityType()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureJson));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null)));
    }

    [Fact]
    public void SupportedEntityTypes_IsCountryOnly()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureJson));

        Assert.Equal(new[] { VerificationEntityType.Country }, source.SupportedEntityTypes);
    }

    [Fact]
    public void Name_And_CostPerRowUsd()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureJson));

        Assert.Equal("mledoze", source.Name);
        Assert.Equal(0m, source.CostPerRowUsd);
    }
}
