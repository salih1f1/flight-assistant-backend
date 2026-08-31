using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Usage;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

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
    public void Validator_Accepts_AnOmittedRange()
    {
        var result = Validator().Validate(new GetAeroApiUsageQuery());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }
}
