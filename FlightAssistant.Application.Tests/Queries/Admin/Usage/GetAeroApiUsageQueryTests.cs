using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlightAssistant.Application.Tests.Queries.Admin.Usage;

/// <summary>
/// The usage query proxies AeroAPI's own spend report. Two things carry all the risk and are what
/// these tests pin down: the resolved range (it is the cache key, so an unstable one would spend a
/// metered call on every dashboard refresh) and what is — and is not — allowed to come out of the
/// cache.
/// </summary>
public class GetAeroApiUsageQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 37, 22, TimeSpan.Zero);
    private static readonly DateTimeOffset MonthStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly AeroApiAccountUsage Usage = new(
        TotalCalls: 42,
        TotalPages: 46,
        TotalCostUsd: 0.66m,
        TotalDiscountCostUsd: 0m,
        TotalSuccessfulCalls: 41,
        TotalFailedCalls: 1,
        ByOperation: [new AeroApiOperationUsage("GET /schedules/{date_start}/{date_end}", 21, 21, 0.42m, 21, 0)]);

    /// <summary>Real dictionary-backed cache rather than a substitute: a stubbed Get/Set pair would
    /// still "hit" even if the handler computed a different key each time, which is precisely the
    /// failure mode worth catching.</summary>
    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, object?> _entries = [];
        public List<(string Key, TimeSpan? Ttl)> Writes { get; } = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            _entries[key] = value;
            Writes.Add((key, absoluteExpire));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBudget : IAeroApiBudgetOptions
    {
        public decimal MonthlyBudgetUsd => 5m;
        public decimal SoftLimitRatio => 0.8m;
    }

    private sealed class FakeThrottleObserver : IAeroApiThrottleObserver
    {
        public DateTimeOffset? NextSlotUtc { get; set; }
    }

    private readonly IAeroApiAccountUsageProvider _provider = Substitute.For<IAeroApiAccountUsageProvider>();
    private readonly FakeCache _cache = new();
    private readonly FakeThrottleObserver _throttle = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly GetAeroApiUsageQueryHandler _handler;

    public GetAeroApiUsageQueryTests()
    {
        _provider
            .GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Usage);

        _handler = new GetAeroApiUsageQueryHandler(_provider, _cache, new FakeBudget(), _throttle, _clock);
    }

    // ── Range resolution ──────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_DefaultsToTheCurrentUtcMonth_WhenNoRangeIsGiven()
    {
        var result = await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(MonthStart, result.RangeStartUtc);
        await _provider.Received(1).GetUsageAsync(MonthStart, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FloorsTheDefaultEnd_OntoTheCacheGrid_SoTheKeyIsStable()
    {
        // 10:37:22 → 10:30:00. Never in the future, and unchanged for the whole 10-minute TTL.
        var expectedEnd = new DateTimeOffset(2026, 9, 17, 10, 30, 0, TimeSpan.Zero);

        var result = await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(expectedEnd, result.RangeEndUtc);
        await _provider.Received(1).GetUsageAsync(MonthStart, expectedEnd, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UsesNowItself_WhenTheFlooredEndWouldPrecedeTheMonthStart()
    {
        // 00:04 on the 1st: flooring would land on the month start and ask for an empty window.
        // Its own clock — FakeTimeProvider refuses to move backwards from the shared one.
        var justAfterMidnight = new DateTimeOffset(2026, 9, 1, 0, 4, 0, TimeSpan.Zero);
        var handler = new GetAeroApiUsageQueryHandler(
            _provider, _cache, new FakeBudget(), _throttle, new FakeTimeProvider(justAfterMidnight));

        var result = await handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(MonthStart, result.RangeStartUtc);
        Assert.Equal(justAfterMidnight, result.RangeEndUtc);
    }

    [Fact]
    public async Task Handle_NormalisesAnExplicitRangeToUtc_AndReportsWhatItAskedFor()
    {
        var start = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.FromHours(2)); // 2026-08-01T00:00Z
        var end = new DateTimeOffset(2026, 9, 1, 2, 0, 0, TimeSpan.FromHours(2));   // 2026-09-01T00:00Z

        var result = await _handler.Handle(new GetAeroApiUsageQuery(start, end), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), result.RangeStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), result.RangeEndUtc);
        await _provider.Received(1).GetUsageAsync(
            result.RangeStartUtc, result.RangeEndUtc, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ClampsAFarFutureExplicitEnd_ToTheSameFlooredNowAsTheDefaultPath()
    {
        // ?end=2099-01-01 must never be forwarded to AeroAPI unverified — the exact behaviour the
        // default path's flooring exists to avoid.
        var result = await _handler.Handle(
            new GetAeroApiUsageQuery(EndUtc: new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        var expectedEnd = new DateTimeOffset(2026, 9, 17, 10, 30, 0, TimeSpan.Zero);
        Assert.Equal(expectedEnd, result.RangeEndUtc);
        await _provider.Received(1).GetUsageAsync(MonthStart, expectedEnd, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FloorsAnExplicitEndInsideTheCurrentBucket_SoItSharesTheDefaultsCacheKey()
    {
        // ?end=<now> is the natural shape of a range-picker UI: it must land on the same key as the
        // defaulted end or every such request would spend a fresh metered call and throttle slot.
        var insideCurrentBucket = new DateTimeOffset(2026, 9, 17, 10, 33, 0, TimeSpan.Zero);

        var withDefault = await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);
        var withExplicitEnd = await _handler.Handle(
            new GetAeroApiUsageQuery(EndUtc: insideCurrentBucket), CancellationToken.None);

        Assert.Equal(withDefault.RangeEndUtc, withExplicitEnd.RangeEndUtc);
        await _provider.Received(1).GetUsageAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        Assert.Single(_cache.Writes);
    }

    [Fact]
    public async Task Handle_KeepsAnExplicitEndFromAnEarlierBucket_Verbatim()
    {
        var earlier = new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

        var result = await _handler.Handle(new GetAeroApiUsageQuery(EndUtc: earlier), CancellationToken.None);

        Assert.Equal(earlier, result.RangeEndUtc);
    }

    [Fact]
    public async Task Handle_TruncatesTheRawNowFallback_ToWholeSeconds()
    {
        // The degenerate-window ("raw now") path is the one that can carry sub-second ticks; the
        // cache key is formatted to second precision, so anything finer would let two requests in
        // the same second share a key while reporting different RangeEndUtc values.
        var justAfterMidnightWithMillis = new DateTimeOffset(2026, 9, 1, 0, 4, 0, 750, TimeSpan.Zero);
        var handler = new GetAeroApiUsageQueryHandler(
            _provider, _cache, new FakeBudget(), _throttle,
            new FakeTimeProvider(justAfterMidnightWithMillis));

        var result = await handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 4, 0, TimeSpan.Zero), result.RangeEndUtc);
    }

    [Fact]
    public async Task Handle_TruncatesTheRawNowFallback_SoTwoRequestsInTheSameSecondShareACacheKey()
    {
        var firstHandler = new GetAeroApiUsageQueryHandler(
            _provider, _cache, new FakeBudget(), _throttle,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 4, 0, 100, TimeSpan.Zero)));
        var first = await firstHandler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        var secondHandler = new GetAeroApiUsageQueryHandler(
            _provider, _cache, new FakeBudget(), _throttle,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 4, 0, 900, TimeSpan.Zero)));
        var second = await secondHandler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(first.RangeEndUtc, second.RangeEndUtc);
        await _provider.Received(1).GetUsageAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        Assert.Single(_cache.Writes);
    }

    /// <summary>
    /// The backstop from the review fix: even called directly (as this unit test does, bypassing
    /// the validator entirely) the handler must never ship an inverted range upstream, regardless of
    /// what a caller — the validator included — decided after sampling a different instant of "now".
    /// </summary>
    [Fact]
    public async Task Handle_ThrowsIfTheResolvedRangeIsInverted_RegardlessOfValidation()
    {
        var query = new GetAeroApiUsageQuery(StartUtc: Now.AddDays(-1), EndUtc: Now.AddDays(-2));

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(query, CancellationToken.None));
        Assert.Equal(400, ex.StatusCode);
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_PopulatesTheCache_OnAMiss_ForTenMinutes()
    {
        await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        var write = Assert.Single(_cache.Writes);
        Assert.Equal(TimeSpan.FromMinutes(10), write.Ttl);
        Assert.Equal(GetAeroApiUsageQueryHandler.CacheDuration, write.Ttl);
    }

    [Fact]
    public async Task Handle_DoesNotCallTheProvider_OnACacheHit()
    {
        await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        // Same 10-minute bucket, so the same resolved range and the same key.
        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        await _provider.Received(1).GetUsageAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        Assert.Equal(Usage.TotalCostUsd, second.TotalCostUsd);
        Assert.Single(_cache.Writes);
    }

    /// <summary>
    /// Pins the other half of what the class header claims: not just what is allowed out of the
    /// cache, but what is kept out of it. A provider failure must never be written, or a transient
    /// upstream error would be served as a confident, cached "answer" for the next 10 minutes.
    /// </summary>
    [Fact]
    public async Task Handle_DoesNotCacheAFailedProviderCall()
    {
        _provider
            .GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException("Usage reporting is temporarily unavailable.", 502));

        await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None));

        Assert.Empty(_cache.Writes);
    }

    [Fact]
    public async Task Handle_CallsTheProviderAgain_OnceTheRangeMovesToTheNextBucket()
    {
        await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(11));
        await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        await _provider.Received(2).GetUsageAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReadsNextSlotUtcLive_EvenOnACacheHit()
    {
        _throttle.NextSlotUtc = Now.AddSeconds(7);
        var first = await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);
        Assert.Equal(Now.AddSeconds(7), first.NextSlotUtc);

        // Throttle state is not part of the cached upstream payload — a hit must not freeze it.
        _throttle.NextSlotUtc = Now.AddSeconds(19);
        var second = await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(Now.AddSeconds(19), second.NextSlotUtc);
    }

    // ── Projection ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ReportsTheProvidersFiguresAndTheConfiguredBudget()
    {
        var result = await _handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(42, result.TotalCalls);
        Assert.Equal(46, result.TotalPages);
        Assert.Equal(0.66m, result.TotalCostUsd);
        Assert.Equal(41, result.TotalSuccessfulCalls);
        Assert.Equal(1, result.TotalFailedCalls);
        Assert.Equal(5m, result.MonthlyBudgetUsd);

        var row = Assert.Single(result.ByOperation);
        Assert.Equal("GET /schedules/{date_start}/{date_end}", row.Operation);
        Assert.Equal(21, row.Pages);
        Assert.Equal(0.42m, row.CostUsd);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private GetAeroApiUsageQueryValidator Validator() => new(_clock);

    [Fact]
    public void Validator_Rejects_AStartEarlierThanOneYearAgo()
    {
        var result = Validator().Validate(new GetAeroApiUsageQuery(Now.AddYears(-1).AddDays(-1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("1 year"));
    }

    [Fact]
    public void Validator_Accepts_AStartInsideTheOneYearWindow()
    {
        var result = Validator().Validate(new GetAeroApiUsageQuery(Now.AddYears(-1).AddDays(1)));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validator_Rejects_AStartAfterItsEnd()
    {
        var result = Validator().Validate(
            new GetAeroApiUsageQuery(Now.AddDays(-1), Now.AddDays(-2)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("End", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_Rejects_AnEndBeforeTheDefaultedStart()
    {
        // End-only request: the start defaults to the first of the current UTC month, so an end in
        // the previous month inverts the range even though no start was supplied to compare with.
        var result = Validator().Validate(new GetAeroApiUsageQuery(EndUtc: MonthStart.AddDays(-3)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("End", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_Rejects_AStartInTheFuture()
    {
        // With no end, a future start would resolve to an inverted range rather than an error.
        var result = Validator().Validate(new GetAeroApiUsageQuery(Now.AddDays(1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("future"));
    }

    [Fact]
    public void Validator_Rejects_AnEndInTheFuture()
    {
        // Mirrors the start-in-the-future rule: ?end=2099-01-01 must be bounced here with a 400
        // rather than silently clamped to "now" downstream with no explanation.
        var result = Validator().Validate(new GetAeroApiUsageQuery(EndUtc: Now.AddDays(1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.PropertyName.Contains("End", StringComparison.OrdinalIgnoreCase)
            && e.ErrorMessage.Contains("future"));
    }

    [Fact]
    public void Validator_Accepts_AnOmittedRange()
    {
        var result = Validator().Validate(new GetAeroApiUsageQuery());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    // ── Cache serialization ───────────────────────────────────────────────────

    [Fact]
    public void CachedPayload_SurvivesAJsonRoundTrip_TheWayRedisCacheServiceStoresIt()
    {
        // The in-memory cache stores the object as-is, so nothing local would ever notice a shape
        // System.Text.Json cannot rebuild — but production runs Redis, which serializes with
        // JsonSerializerDefaults.Web. A cached record whose IReadOnlyList could not round-trip
        // would fail only there, on a deploy, as a 500 on the dashboard.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var json = System.Text.Json.JsonSerializer.Serialize(Usage, options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AeroApiAccountUsage>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(Usage.TotalCalls, restored!.TotalCalls);
        Assert.Equal(Usage.TotalCostUsd, restored.TotalCostUsd);
        var row = Assert.Single(restored.ByOperation);
        Assert.Equal(Usage.ByOperation[0], row);
    }
}
