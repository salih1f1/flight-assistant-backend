using System.Globalization;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlightAssistant.Application.Tests.ProviderBudget;

/// <summary>
/// The guard compares this month's AeroAPI spend with the budget (#303). What carries the risk: the
/// thresholds, reading the same cached report as the admin gauge (so a check costs no extra call), and
/// failing open without retrying a failed read on every check.
/// </summary>
public class ProviderBudgetGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 37, 22, TimeSpan.Zero);
    private static readonly DateTimeOffset MonthStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NowOnTheCacheGrid = new(2026, 9, 17, 10, 30, 0, TimeSpan.Zero);

    // Non-default values (#308): a $20 budget with the soft limit at 60%, so $12.
    private sealed class FakeBudget : IAeroApiBudgetOptions
    {
        public decimal MonthlyBudgetUsd => 20m;
        public decimal SoftLimitRatio => 0.6m;
    }

    /// <summary>A dictionary cache that ignores expiry, records every write, and can be made to fail.</summary>
    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, object?> _entries = [];
        public List<(string Key, TimeSpan? Ttl)> Writes { get; } = [];
        public bool Fail { get; init; }

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("cache unavailable");
            return Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("cache unavailable");
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

    private readonly IAeroApiAccountUsageProvider _provider = Substitute.For<IAeroApiAccountUsageProvider>();
    private readonly FakeCache _cache = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly CapturingLogger<ProviderBudgetGuard> _logger = new();

    private ProviderBudgetGuard Guard(ICacheService? cache = null) =>
        new(_provider, cache ?? _cache, new FakeBudget(), _clock, _logger);

    private void SpendIs(decimal costUsd) =>
        _provider.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AeroApiAccountUsage(
                TotalCalls: 10,
                TotalPages: 12,
                TotalCostUsd: costUsd,
                TotalDiscountCostUsd: 0m,
                TotalSuccessfulCalls: 10,
                TotalFailedCalls: 0,
                ByOperation: []));

    private Task ReceivedUsageCalls(int count) =>
        _provider.Received(count).GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

    [Theory]
    [InlineData("0", ProviderBudgetState.Normal)]
    [InlineData("11.99", ProviderBudgetState.Normal)]
    [InlineData("12.00", ProviderBudgetState.SoftLimit)] // exactly 60% of $20
    [InlineData("19.99", ProviderBudgetState.SoftLimit)]
    [InlineData("20.00", ProviderBudgetState.Exhausted)] // exactly the budget
    [InlineData("31.40", ProviderBudgetState.Exhausted)]
    public async Task GetStateAsync_ComparesThisMonthsSpend_WithTheBudget(string spentUsd, ProviderBudgetState expected)
    {
        SpendIs(decimal.Parse(spentUsd, CultureInfo.InvariantCulture));

        Assert.Equal(expected, await Guard().GetStateAsync());
    }

    [Fact]
    public async Task GetStateAsync_ReadsTheMonthToDate_OnTheUsageReportsCacheGrid()
    {
        SpendIs(1m);

        await Guard().GetStateAsync();

        await _provider.Received(1).GetUsageAsync(MonthStart, NowOnTheCacheGrid, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStateAsync_SharesTheAdminUsageReportsCacheEntry()
    {
        SpendIs(1m);
        await Guard().GetStateAsync();

        // The admin gauge's default view, over the same cache: it must be a hit.
        var handler = new GetAeroApiUsageQueryHandler(
            _provider, _cache, new FakeBudget(), Substitute.For<IAeroApiThrottleObserver>(), _clock);
        var report = await handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(1m, report.TotalCostUsd);
        await ReceivedUsageCalls(1);
    }

    [Fact]
    public async Task GetStateAsync_ACachedReport_IsNotFetchedAgain()
    {
        SpendIs(1m);
        var guard = Guard();

        await guard.GetStateAsync();
        await guard.GetStateAsync();

        await ReceivedUsageCalls(1);
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00Z")]
    [InlineData("2026-10-01T00:09:59Z")]
    public async Task GetStateAsync_InAMonthsFirstTenMinutes_IsNormal_WithoutAReport(string now)
    {
        _clock.SetUtcNow(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));
        SpendIs(50m);

        Assert.Equal(ProviderBudgetState.Normal, await Guard().GetStateAsync());
        await ReceivedUsageCalls(0);
    }

    [Fact]
    public async Task GetStateAsync_TenMinutesIntoAMonth_ReadsTheReport()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 1, 0, 10, 0, TimeSpan.Zero));
        SpendIs(50m);

        Assert.Equal(ProviderBudgetState.Exhausted, await Guard().GetStateAsync());
    }

    [Fact]
    public async Task GetStateAsync_AFailedRead_IsNormal_AndIsNotRetriedForTenMinutes()
    {
        _provider.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException("Usage reporting is temporarily unavailable.", 502));
        var guard = Guard();

        Assert.Equal(ProviderBudgetState.Normal, await guard.GetStateAsync());
        Assert.Equal(ProviderBudgetState.Normal, await guard.GetStateAsync());

        await ReceivedUsageCalls(1);
        Assert.Contains(_cache.Writes, w => w.Key == "aeroapi:budget-guard:read-failed" && w.Ttl == TimeSpan.FromMinutes(10));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task GetStateAsync_ACacheOutage_IsNormal_WithoutAReport()
    {
        SpendIs(50m);

        Assert.Equal(ProviderBudgetState.Normal, await Guard(new FakeCache { Fail = true }).GetStateAsync());
        await ReceivedUsageCalls(0);
    }

    [Fact]
    public async Task GetStateAsync_Cancelled_Throws_AndRemembersNoFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _provider.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Guard().GetStateAsync(cts.Token));
        Assert.Empty(_cache.Writes);
    }

    [Fact]
    public async Task GetStateAsync_PastTheSoftLimit_WarnsOncePerFreshReport()
    {
        SpendIs(12m);
        var guard = Guard();

        await guard.GetStateAsync();
        await guard.GetStateAsync();

        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task GetStateAsync_BelowTheSoftLimit_LogsNoWarning()
    {
        SpendIs(11.99m);

        await Guard().GetStateAsync();

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
