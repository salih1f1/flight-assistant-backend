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
/// Drives <see cref="OurAirportsVerificationSource"/> against a small recorded slice of the real
/// OurAirports CSV (<c>FlightAssistant.Infrastructure.Tests/Fixtures/Verification/ourairports-slice.csv</c>,
/// header kept verbatim, text fields quoted the way the live file quotes them) via a fake
/// <see cref="HttpMessageHandler"/> -- #367 task 3. No live network call is made by any test in this file.
/// </summary>
public class OurAirportsVerificationSourceTests
{
    private static readonly string FixtureCsv =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Verification", "ourairports-slice.csv"));

    /// <summary>A short, test-only download timeout -- <see cref="OurAirportsVerificationSource.DownloadTimeout"/>
    /// (3 minutes) would make a timeout test itself take 3 real minutes.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(100);

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

    /// <summary>Never sends a response at all -- simulates a server that never even answers, so the
    /// download's own deadline (not the caller's cancellation) must be what ends the call.</summary>
    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable — the delay above never completes normally.");
        }
    }

    /// <summary>Answers with headers immediately (200 OK) but a body stream whose reads never
    /// complete -- fix round 1, item 1's "stalled body" scenario, which
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> makes possible: the send itself
    /// succeeds, only the body hangs.</summary>
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
    /// wait deterministically for "the shared download has started" instead of an arbitrary sleep
    /// (fix round 1, item 10c).</summary>
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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FixtureCsv, Encoding.UTF8, "text/csv") };
        }
    }

    /// <summary>Asserts every <see cref="CreateClient"/> call names
    /// <see cref="OurAirportsVerificationSource.HttpClientName"/> (fix round 2, item 1) -- a source
    /// that requested the wrong (or default/unnamed) client would fail every test in this file, not
    /// just the DI-level ones.</summary>
    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public int ClientsCreated { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(OurAirportsVerificationSource.HttpClientName, name);
            ClientsCreated++;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static OurAirportsVerificationSource Source(
        HttpMessageHandler handler, TimeProvider? timeProvider = null, TimeSpan? downloadTimeout = null) =>
        new(new FakeHttpClientFactory(handler), timeProvider ?? TimeProvider.System,
            NullLogger<OurAirportsVerificationSource>.Instance, downloadTimeout ?? TestTimeout);

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
    public async Task LookupAsync_ParsesAQuotedNameContainingAComma()
    {
        // Fix round 1, item 9: the real file quotes text fields, and a quoted field can itself
        // contain a comma without breaking column alignment.
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("SAM", null));

        Assert.True(result.Lookup!.IsFound);
        Assert.Equal("Sample Airport, Terminal 2", result.Lookup.Values!["Name"]);
        Assert.Equal("US", result.Lookup.Values!["CountryCode"]); // the next column wasn't swallowed by the comma
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
    public async Task LookupAsync_PicksTheHighestScore_AmongDuplicateIataRows_HigherScoreFirst()
    {
        // Fix round 1, item 3: the fixture lists the higher-score row ("Dup High", score 9) BEFORE
        // the lower one ("Dup Low", score 5) precisely so a "last one wins" bug (unconditional
        // `index[key] = row`) would return "Dup Low" here.
        //
        // Fix round 2, item 5: on its own this arrangement could NOT catch a "first one wins" bug
        // (TryAdd) either -- both a correct implementation and a first-wins one return "Dup High"
        // from this specific ordering, since the higher score also happens to be first. See
        // LookupAsync_PicksTheHighestScore_AmongDuplicateIataRows_LowerScoreFirst below for the
        // complementary ordering that catches first-wins instead.
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("DUP", null));

        Assert.True(result.Lookup!.IsFound);
        Assert.Equal("Dup High", result.Lookup.Values!["Name"]);
    }

    [Fact]
    public async Task LookupAsync_PicksTheHighestScore_AmongDuplicateIataRows_LowerScoreFirst()
    {
        // Fix round 2, item 5: the complementary ordering to the test above -- "Tre Low" (score 4)
        // listed BEFORE "Tre High" (score 11), so a "first one wins" bug (Dictionary.TryAdd, or
        // equivalent) would return "Tre Low" here. Together, the two IATA tests reject both a
        // first-wins and a last-wins implementation without relying on the ICAO index at all.
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("TRE", null));

        Assert.True(result.Lookup!.IsFound);
        Assert.Equal("Tre High", result.Lookup.Values!["Name"]);
    }

    [Fact]
    public async Task LookupAsync_PicksTheHighestScore_AmongDuplicateIcaoRows()
    {
        // Fix round 2, item 5: the ICAO index's own KeepHighestScore call site, arranged
        // low-score-first ("Dup Icao Low" score 3, then "Dup Icao High" score 8) -- the opposite
        // order from the IATA "higher score first" test above, so between the two indices both a
        // first-wins and a last-wins bug are rejected by at least one test.
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport(null, "KDPI"));

        Assert.True(result.Lookup!.IsFound);
        Assert.Equal("Dup Icao High", result.Lookup.Values!["Name"]);
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
    public async Task LookupAsync_ReturnsASourceError_WhenNothingAnswers()
    {
        var source = Source(new NeverRespondingHandler());

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_WhenTheBodyStalls()
    {
        // Fix round 1, item 1: headers arrive (200 OK) but the body never completes. Before the
        // fix, HttpClient.Timeout only bounded time-to-headers -- ResponseHeadersRead makes the
        // send itself return the instant headers land, so nothing bounded the subsequent parse.
        // The outer WaitAsync is a test-level safety net (per the review's own suggestion): if the
        // fix regresses, this fails fast with a timeout instead of hanging the whole test run.
        var source = Source(new StalledBodyHandler());

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null))
            .WaitAsync(TimeSpan.FromSeconds(5));

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
        // Pins that a 429 is a source error, never NotFound. IsError alone already distinguishes
        // the two (IsError is defined as Error is not null, and NotFound carries a null Error), so
        // Assert.True(result.IsError) on its own already fails for a NotFound result -- the
        // Assert.Null(result.Lookup) below is not needed to *distinguish* NotFound from a source
        // error; it simply pins the type's own invariant that a source-error result never also
        // carries a Lookup value.
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

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnAnEmptyBody()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, string.Empty));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnAHeaderOnlyCsv()
    {
        // Fix round 1, item 6 (comment corrected in fix round 2, item 7): a syntactically valid CSV
        // with the real header but zero data rows parses without throwing, so only the "zero
        // indexed rows" check catches it. Without that check this would incorrectly succeed with an
        // empty (but "loaded") dataset, making every subsequent lookup a false NotFound.
        //
        // This is NOT what a renamed iata_code/icao_code column looks like -- CsvHelper's own
        // header validation rejects that before any row is read (every CsvRow column is required,
        // none are Optional()), which the generic parse-failure catch wraps as "Could not parse the
        // ourairports dataset: ...". LookupAsync_ReturnsASourceError_OnGarbageContent below exercises
        // that same header-validation path (via a header that matches no required column).
        var headerOnly = FixtureCsv[..FixtureCsv.IndexOf('\n')] + "\n";
        var source = Source(new StaticHandler(HttpStatusCode.OK, headerOnly));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
    }

    [Fact]
    public async Task LookupAsync_ReturnsASourceError_OnAHandlerLevelNetworkFailure()
    {
        var source = Source(new ThrowingHandler(new HttpRequestException("connection reset by 10.20.30.40")));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(result.IsError);
        Assert.Null(result.Lookup);
        Assert.Equal("Failed to download the ourairports dataset (network error)", result.Error);
    }

    [Fact]
    public async Task LookupAsync_AnUnparseableDataset_ReturnsTheFixedCategory_AndLogsTheWholeExceptionOnceWithTheSourceName()
    {
        // Final fix wave, C1: CsvHelper's header-validation message describes the file's own content,
        // so only the category is stored; the detail goes to one log line per failed load.
        const string body = "secret_col,other_col\nsecret-record-content,10.20.30.40\n";
        var logger = new CapturingLogger<OurAirportsVerificationSource>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new OurAirportsVerificationSource(
            new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.OK, body)), clock, logger, TestTimeout);

        var first = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        var second = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("DUP", null));

        Assert.Equal("Could not parse the ourairports dataset", first.Error);
        Assert.Equal(first.Error, second.Error);
        Assert.DoesNotContain("secret", first.Error);

        var logged = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("ourairports", logged.Message);
        var exception = Assert.IsType<VerificationSourceFetchException>(logged.Exception);
        Assert.NotNull(exception.InnerException); // CsvHelper's own exception, with its detail
    }

    [Fact]
    public async Task LookupAsync_SkipsMalformedRows_KeepsTheGoodOnes_AndLogsOneWarningWithTheCount()
    {
        // Final fix wave, C2: one malformed row must not fail the whole dataset.
        var lines = FixtureCsv.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var csv = string.Join("\n",
            lines[0], // the real header
            lines[1], // LHR
            // Text after a closing quote: CsvHelper's BadDataFound.
            "5,BADQ,small_airport,\"Bad\" Quote Field,1,1,10,,,US,,,,\"Town\",no,,KBDQ,BDQ,,,,,5,2020-01-01",
            // Cut short after iata_code: CsvHelper's MissingFieldFound, when score is read.
            "6,MISS,small_airport,\"Missing Fields\",1,1,10,,,US,,,,\"Town\",no,,KMIS,MIS",
            // An unquoted comma in the name: one field too many, so "KSHF" lands in iata_code.
            "7,SHFT,small_airport,Shifted, Name,1,1,10,,,US,,,,\"Town\",no,,KSHF,SHF,,,,,5,2020-01-01",
            "8,EGKK,large_airport,\"London Gatwick Airport\",51.1481,-0.1903,202,EU,\"United Kingdom\",GB,\"England\",GB-ENG,,\"London\",yes,EGKK,EGKK,LGW,,,,,800,2024-01-01",
            "");
        var logger = new CapturingLogger<OurAirportsVerificationSource>();
        var source = new OurAirportsVerificationSource(
            new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.OK, csv)), TimeProvider.System, logger, TestTimeout);

        async Task<VerificationSourceFetchResult> Lookup(string iata) =>
            await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport(iata, null));

        Assert.Equal("London Heathrow Airport", (await Lookup("LHR")).Lookup!.Values!["Name"]);
        Assert.Equal("London Gatwick Airport", (await Lookup("LGW")).Lookup!.Values!["Name"]);
        foreach (var skippedCode in new[] { "BDQ", "MIS", "KSHF", "SHF" })
        {
            var result = await Lookup(skippedCode);
            Assert.False(result.IsError, skippedCode);
            Assert.False(result.Lookup!.IsFound, skippedCode);
        }

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Skipped 3 malformed ourairports row(s)", warning.Message);
        Assert.Contains("3, 4, 5", warning.Message);
    }

    [Fact]
    public async Task LookupAsync_ADatasetDeclaringMoreThanMaxDownloadBytes_IsASourceError_WithoutReadingIt()
    {
        // Final fix wave, C5: proves the production cap is the one in use, without a 64 MB body.
        var source = Source(new DeclaredLengthHandler(OurAirportsVerificationSource.MaxDownloadBytes + 1));

        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.Equal(64L * 1024 * 1024, OurAirportsVerificationSource.MaxDownloadBytes);
        Assert.Equal("The ourairports dataset is larger than the 67108864-byte download limit", result.Error);
    }

    [Fact]
    public async Task HasCachedData_IsFalseUntilALookupLoadsTheDataset_AndAgainAfterReleaseCache()
    {
        var source = Source(new StaticHandler(HttpStatusCode.OK, FixtureCsv));
        Assert.False(source.HasCachedData);

        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        Assert.True(source.HasCachedData);

        source.ReleaseCache();
        Assert.False(source.HasCachedData);
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

    /// <summary>Records every log call made through it, with its exception.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    [Fact]
    public async Task LookupAsync_PropagatesCancellation_RatherThanReturningASourceError()
    {
        // The caller's own cancellation is not a source error -- it must propagate as-is, distinct
        // from every failure mode above.
        var source = Source(new NeverRespondingHandler(), downloadTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var lookup = source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
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

    // ── Failure cooldown (final fix wave, A) ─────────────────────────────────

    [Fact]
    public async Task LookupAsync_WithinTheFailureCooldown_ReturnsASourceError_WithoutAnotherDownload()
    {
        var handler = new StaticHandler(HttpStatusCode.ServiceUnavailable, "down");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        var first = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        clock.Advance(VerificationDatasetCache.FailureCooldown - TimeSpan.FromSeconds(1));
        var second = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("DUP", null));

        Assert.True(first.IsError);
        Assert.True(second.IsError);
        Assert.Equal(first.Error, second.Error);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_AfterTheFailureCooldown_DownloadsAgain()
    {
        var handler = new StaticHandler(HttpStatusCode.ServiceUnavailable, "down");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        clock.Advance(VerificationDatasetCache.FailureCooldown);
        var second = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.True(second.IsError);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ReleaseCache_DuringTheFailureCooldown_LetsTheNextLookupDownload()
    {
        var handler = new StaticHandler(HttpStatusCode.ServiceUnavailable, "down");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(handler, clock);

        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        source.ReleaseCache();
        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_TwoCallsThatBothMissTheCache_ShareOneDownload()
    {
        // Deterministic (fix round 1, item 10c): waits for proof the transport was actually
        // reached, rather than an arbitrary sleep, before issuing the second call and releasing.
        var handler = new GatedHandler();
        var source = Source(handler);

        var first = source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));
        await handler.WaitUntilEnteredAsync();
        var second = source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("DUP", null));

        handler.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.CallCount);
        Assert.True(results[0].Lookup!.IsFound);
        Assert.True(results[1].Lookup!.IsFound);
    }

    [Fact]
    public async Task LookupAsync_CancellingOneCallersRequest_DoesNotAffectAJoinedCaller()
    {
        // Fix round 1, item 7: the shared download must not be aborted just because one of the
        // callers joined on it gave up. Fix round 2, item 6: test-level timeouts on the two key
        // awaits, so a regression fails fast rather than potentially waiting out a much longer
        // deadline.
        var handler = new GatedHandler();
        var source = Source(handler);
        using var firstCts = new CancellationTokenSource();

        var first = source.LookupAsync(
            VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null), firstCts.Token);
        await handler.WaitUntilEnteredAsync();
        var second = source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("DUP", null));

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
        // Fix round 1, item 5: the source is a singleton that must not capture one HttpClient (and
        // therefore one HttpMessageHandler) for the whole process lifetime.
        var factory = new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.OK, FixtureCsv));
        var source = new OurAirportsVerificationSource(
            factory, TimeProvider.System, NullLogger<OurAirportsVerificationSource>.Instance, TestTimeout);
        Assert.Equal(0, factory.ClientsCreated);

        await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.Equal(1, factory.ClientsCreated);
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
