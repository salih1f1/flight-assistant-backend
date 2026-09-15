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

    /// <summary>A short, test-only download timeout -- <see cref="MledozeCountriesVerificationSource.DownloadTimeout"/>
    /// (30 seconds) would make a timeout test noticeably slow.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(100);

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

    /// <summary>Answers with headers immediately (200 OK) but a body stream whose reads never
    /// complete -- fix round 1, item 1's "stalled body" scenario.</summary>
    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverCompletingStream()) });
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0; // unreachable
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Blocks every request until <see cref="Release"/> is called, and exposes a task that
    /// completes the instant the <b>first</b> request actually reaches the transport -- lets a test
    /// wait deterministically for "the shared download has started" instead of an arbitrary sleep.
    /// Fix round 2, item 6: this file had no such handler in round 1, so the "two calls share one
    /// download" and "cancelling one caller doesn't affect a joined caller" behaviours (fix round 1,
    /// items 7 and 10c) had no mledoze-side proof, unlike OurAirports's file.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public Task WaitUntilEnteredAsync() => _entered.Task;
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _entered.TrySetResult();
            await _release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FixtureJson, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    /// <summary>Asserts every <see cref="CreateClient"/> call names
    /// <see cref="MledozeCountriesVerificationSource.HttpClientName"/> (fix round 2, item 1) -- a
    /// source that requested the wrong (or default/unnamed) client would fail every test in this
    /// file, not just the DI-level ones.</summary>
    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public int ClientsCreated { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(MledozeCountriesVerificationSource.HttpClientName, name);
            ClientsCreated++;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static MledozeCountriesVerificationSource Source(
        HttpMessageHandler handler, TimeProvider? timeProvider = null, TimeSpan? downloadTimeout = null) =>
        new(new FakeHttpClientFactory(handler), timeProvider ?? TimeProvider.System,
            NullLogger<MledozeCountriesVerificationSource>.Instance, downloadTimeout ?? TestTimeout);

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
    public async Task LookupAsync_ReturnsASourceError_WhenNothingAnswers()
    {
        var source = Source(new NeverRespondingHandler());

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_WhenTheBodyStalls()
    {
        // Fix round 1, item 1: see the OurAirports equivalent for the full rationale.
        var source = Source(new StalledBodyHandler());

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"))
            .WaitAsync(TimeSpan.FromSeconds(5));

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

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnAnEmptyBody()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, string.Empty));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnAnEmptyArray()
    {
        // Fix round 1, item 6: syntactically valid, zero usable rows -- e.g. cca2 renamed upstream.
        // Without the zero-rows check this would succeed with an empty "loaded" dataset, and every
        // lookup would come back a false NotFound instead of flagging the real problem.
        var source = Source(new StaticHandler(HttpStatusCode.OK, "[]"));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_WhenARecordIsMissingCca2OrNameCommon()
    {
        // A second, well-formed record (GB) alongside the malformed one (TR, no name.common) is
        // what makes this distinguishing: a "silently skip the bad record" implementation would
        // still leave a non-empty, successfully "loaded" dataset (GB alone), so the zero-rows check
        // (a separate rule) would never fire and a TR lookup would quietly come back NotFound
        // instead of the required source error.
        const string json = """
            [
              {"cca2":"TR","cca3":"TUR","ccn3":"792","region":"Asia","subregion":"Western Asia","name":{"official":"Republic of Türkiye"}},
              {"cca2":"GB","cca3":"GBR","ccn3":"826","region":"Europe","subregion":"Northern Europe","name":{"common":"United Kingdom","official":"United Kingdom of Great Britain and Northern Ireland"}}
            ]
            """;
        var source = Source(new StaticHandler(HttpStatusCode.OK, json));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnAHandlerLevelNetworkFailure()
    {
        var source = Source(new ThrowingHandler(new HttpRequestException("connection reset")));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_PropagatesCancellation_RatherThanReturningASourceError()
    {
        var source = Source(new NeverRespondingHandler(), downloadTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var lookup = source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
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

    [Fact]
    public async Task LookupAsync_TwoCallsThatBothMissTheCache_ShareOneDownload()
    {
        // Fix round 2, item 6 (parity with OurAirportsVerificationSourceTests -- see GatedHandler's
        // remark): deterministic via GatedHandler, waiting for proof the transport was actually
        // reached rather than an arbitrary sleep.
        var handler = new GatedHandler();
        var source = Source(handler);

        var first = source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));
        await handler.WaitUntilEnteredAsync();
        var second = source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("CI"));

        handler.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.CallCount);
        Assert.True(results[0].Lookup!.IsFound);
        Assert.True(results[1].Lookup!.IsFound);
    }

    [Fact]
    public async Task LookupAsync_CancellingOneCallersRequest_DoesNotAffectAJoinedCaller()
    {
        // Fix round 2, item 6 (parity with OurAirportsVerificationSourceTests): the shared download
        // must not be aborted just because one of the callers joined on it gave up. Test-level
        // timeouts on the two key awaits so a regression fails fast.
        var handler = new GatedHandler();
        var source = Source(handler);
        using var firstCts = new CancellationTokenSource();

        var first = source.LookupAsync(
            VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"), firstCts.Token);
        await handler.WaitUntilEnteredAsync();
        var second = source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("CI"));

        firstCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(TimeSpan.FromSeconds(5));

        handler.Release();
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(secondResult.Lookup!.IsFound);
        Assert.Equal(1, handler.CallCount);
    }

    // ── DI wiring ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LookupAsync_CreatesTheHttpClientFreshFromTheFactory_NotOnceAtConstruction()
    {
        var factory = new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.OK, FixtureJson));
        var source = new MledozeCountriesVerificationSource(
            factory, TimeProvider.System, NullLogger<MledozeCountriesVerificationSource>.Instance, TestTimeout);
        Assert.Equal(0, factory.ClientsCreated);

        await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.Equal(1, factory.ClientsCreated);
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
