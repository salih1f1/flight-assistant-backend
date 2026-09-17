using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The download deadline for the tests that hold <see cref="GatedHandler"/> closed (#431).
    /// Those tests are about joining a shared download and per-caller cancellation, not about the
    /// deadline. With <see cref="TestTimeout"/>'s 100 ms, a loaded CI runner could keep the gate
    /// closed past it: the shared load then timed out, and the joined caller got a source error.
    /// </summary>
    private static readonly TimeSpan GatedDownloadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the gated tests deliberately keep the gate closed before releasing it: longer than
    /// <see cref="TestTimeout"/>, so a gated test that loses its own deadline fails on every run
    /// rather than only on a slow one (#431).
    /// </summary>
    private static readonly TimeSpan GateHold = TestTimeout * 3;

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
        var source = Source(new StaticHandler(HttpStatusCode.OK, "not json at all {{{ secret-record-content"));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        // Final fix wave, C1: the JsonException's message (which can quote the content) is not stored.
        Assert.Equal("Could not parse the mledoze dataset", result.Error);
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
        // A syntactically valid, genuinely empty dataset. Distinct from a renamed cca2/name.common
        // field (fix round 2, item 8), which no longer fails the whole dataset -- each record
        // missing one of those is skipped individually, and only zero *surviving* records (this
        // case, or every record turning out bad) is a source error.
        var source = Source(new StaticHandler(HttpStatusCode.OK, "[]"));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_SkipsARecordMissingCca2OrNameCommon_LeavingOthersUsable()
    {
        // Fix round 2, item 8 (controller ruling -- reverses round 1's item 6 rule for mledoze): one
        // malformed upstream record must not fail the whole dataset, or every country's
        // verification becomes a source error that keeps retrying and never clears. GB survives and
        // is found; the malformed TR record is simply absent from the index, so a TR lookup comes
        // back NotFound (visible to an admin), not a source error.
        const string json = """
            [
              {"cca2":"TR","cca3":"TUR","ccn3":"792","region":"Asia","subregion":"Western Asia","name":{"official":"Republic of Türkiye"}},
              {"cca2":"GB","cca3":"GBR","ccn3":"826","region":"Europe","subregion":"Northern Europe","name":{"common":"United Kingdom","official":"United Kingdom of Great Britain and Northern Ireland"}}
            ]
            """;
        var source = Source(new StaticHandler(HttpStatusCode.OK, json));

        var gbResult = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("GB"));
        var trResult = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.False(gbResult.IsError);
        Assert.True(gbResult.Lookup!.IsFound);
        Assert.Equal("United Kingdom", gbResult.Lookup.Values!["Name"]);

        Assert.False(trResult.IsError);
        Assert.False(trResult.Lookup!.IsFound); // skipped, not a source error
    }

    [Fact]
    public async Task LookupAsync_SkippedRecords_AreLoggedOnceWithTheirCountAndTheFirstFew()
    {
        // Final fix wave, A: one warning per parse, not one per record.
        const string json = """
            [
              {"cca3":"AAA","name":{"common":"No Code A"}},
              {"cca2":"GB","cca3":"GBR","ccn3":"826","region":"Europe","subregion":"Northern Europe","name":{"common":"United Kingdom"}},
              {"cca2":"BB","cca3":"BBB","name":{"official":"No Common B"}},
              {"cca3":"CCC","name":{"common":"No Code C"}},
              {"cca3":"DDD","name":{"common":"No Code D"}}
            ]
            """;
        var logger = new CapturingLogger<MledozeCountriesVerificationSource>();
        var source = new MledozeCountriesVerificationSource(
            new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.OK, json)), TimeProvider.System, logger, TestTimeout);

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("GB"));

        Assert.True(result.Lookup!.IsFound);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Skipped 4 mledoze record(s)", warning.Message);
        Assert.Contains("0/AAA, 2/BBB, 3/CCC", warning.Message);
        Assert.DoesNotContain("DDD", warning.Message);
    }

    [Fact]
    public async Task LookupAsync_WithinTheFailureCooldown_ReturnsASourceError_WithoutAnotherDownload()
    {
        // Final fix wave, A; the full cooldown behaviour is covered on VerificationDatasetCacheTests
        // and OurAirportsVerificationSourceTests -- this proves mledoze goes through the same cache.
        var handler = new StaticHandler(HttpStatusCode.ServiceUnavailable, "down");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        var first = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));
        clock.Advance(VerificationDatasetCache.FailureCooldown - TimeSpan.FromSeconds(1));
        var second = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("GB"));

        Assert.True(first.IsError);
        Assert.True(second.IsError);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_ADatasetDeclaringMoreThanMaxDownloadBytes_IsASourceError_WithoutReadingIt()
    {
        // Final fix wave, C5: proves the production cap is the one in use, without an 8 MB body.
        var source = Source(new DeclaredLengthHandler(MledozeCountriesVerificationSource.MaxDownloadBytes + 1));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.Equal(8L * 1024 * 1024, MledozeCountriesVerificationSource.MaxDownloadBytes);
        Assert.Equal("The mledoze dataset is larger than the 8388608-byte download limit", result.Error);
    }

    /// <summary>Answers 200 with content that declares <paramref name="length"/> bytes and has none.</summary>
    private sealed class DeclaredLengthHandler(long length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new DeclaredLengthContent(length) });
    }

    private sealed class DeclaredLengthContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long computed)
        {
            computed = length;
            return true;
        }
    }

    /// <summary>Records every log call made through it.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_WhenEveryRecordIsBad()
    {
        // Fix round 2, item 8: the one case where a bad record still ends in a source error -- when
        // *nothing* survives (here, the only record has no cca2 at all).
        const string json = """[{"cca3":"TUR","ccn3":"792","region":"Asia","subregion":"Western Asia","name":{"common":"Turkey"}}]""";
        var source = Source(new StaticHandler(HttpStatusCode.OK, json));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnAHandlerLevelNetworkFailure()
    {
        var source = Source(new ThrowingHandler(new HttpRequestException("connection reset by 10.20.30.40")));

        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
        Assert.Equal("Failed to download the mledoze dataset (network error)", result.Error);
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
        source.ReleaseCache(TimeSpan.Zero);
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
        var source = Source(handler, downloadTimeout: GatedDownloadTimeout);

        var first = source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"));
        await handler.WaitUntilEnteredAsync();
        var second = source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("CI"));

        await Task.Delay(GateHold);
        handler.Release();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

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
        var source = Source(handler, downloadTimeout: GatedDownloadTimeout);
        using var firstCts = new CancellationTokenSource();

        var first = source.LookupAsync(
            VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("TR"), firstCts.Token);
        await handler.WaitUntilEnteredAsync();
        var second = source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("CI"));

        firstCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(GateHold);
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
