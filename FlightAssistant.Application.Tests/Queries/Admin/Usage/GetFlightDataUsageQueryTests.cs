using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Domain.Flights.Enums;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Usage;

/// <summary>
/// #503 pairs AeroAPI's own spend with our own sync activity, so #364's Phase 4 can be decided from
/// numbers. What carries the risk here is not the arithmetic but the claims the shape makes: that it
/// shares the gauge's cached upstream read rather than spending a second throttle slot, that the
/// denominator counts flights rather than tracking links, and that a derived figure is never
/// presented as a measured per-flight cost.
/// </summary>
public class GetFlightDataUsageQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 37, 22, TimeSpan.Zero);

    private static readonly AeroApiAccountUsage Usage = new(
        TotalCalls: 40,
        TotalPages: 44,
        TotalCostUsd: 2.00m,
        TotalDiscountCostUsd: 0m,
        TotalSuccessfulCalls: 39,
        TotalFailedCalls: 1,
        ByOperation: [new AeroApiOperationUsage("GET /flights/{ident}", 30, 30, 1.50m, 30, 0)]);

    /// <summary>Real dictionary-backed cache, so a handler that computed a different key than the
    /// gauge does would miss rather than silently "hit" — the whole point of the shared entry.</summary>
    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, object?> _entries = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            _entries[key] = value;
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

    private readonly IAeroApiAccountUsageProvider _provider = Substitute.For<IAeroApiAccountUsageProvider>();
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IFlightSyncOptions _syncOptions = Substitute.For<IFlightSyncOptions>();
    private readonly FakeCache _cache = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public GetFlightDataUsageQueryTests()
    {
        _provider
            .GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Usage);
        _syncOptions.StopAfterArrival.Returns(TimeSpan.FromMinutes(30));
        _syncOptions.StopWithoutArrivalAfter.Returns(TimeSpan.FromDays(1));
    }

    private GetFlightDataUsageQueryHandler CreateHandler() =>
        new(_provider, _cache, _flights, new FakeBudget(), _syncOptions, _clock);

    [Fact]
    public async Task ItPairsProviderSpendWithOurOwnActivity()
    {
        _flights.CountActiveTrackedFlightsAsync(
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(4);
        _flights.CountTrackedFlightsByStoredSyncModeAsync(Arg.Any<CancellationToken>())
            .Returns([(FlightSyncMode.Live, 3), (FlightSyncMode.Fast, 1)]);
        _flights.CountStatusEventsBySourceAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([("AeroAPI", 8, 4)]);

        var result = await CreateHandler().Handle(new GetFlightDataUsageQuery(), CancellationToken.None);

        // Spend passes straight through from the provider — never recomputed here.
        Assert.Equal(2.00m, result.TotalCostUsd);
        Assert.Equal(44, result.TotalPages);
        Assert.Equal(5m, result.MonthlyBudgetUsd);

        Assert.Equal(4, result.ActiveTrackedFlights);
        Assert.Equal(8, result.TotalEventsRecorded);
        Assert.Equal("AeroAPI", Assert.Single(result.EventYield).Source);

        // $2.00 over 4 flights and 8 events.
        Assert.Equal(0.50m, result.Derived.CostUsdPerActiveTrackedFlight);
        Assert.Equal(0.25m, result.Derived.CostUsdPerRecordedEvent);
    }

    /// <summary>
    /// The window defaults to the current UTC month to date and resolves through the same
    /// <c>UsageRange</c> the gauge uses, so both land on one cache entry. Asserted by calling the
    /// handler twice and checking the provider was hit once: a handler that computed its own range,
    /// or its own key, would go upstream both times.
    /// </summary>
    [Fact]
    public async Task ASecondCallInTheSameWindow_ReusesTheGaugesCachedRead()
    {
        StubLocalReads();
        var handler = CreateHandler();

        var first = await handler.Handle(new GetFlightDataUsageQuery(), CancellationToken.None);
        await handler.Handle(new GetFlightDataUsageQuery(), CancellationToken.None);

        await _provider.Received(1).GetUsageAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), first.RangeStartUtc);
    }

    /// <summary>
    /// Zero tracked flights must not render as "$0.00 per flight" — there is no per-flight figure to
    /// report, and a panel dividing by it would show an infinity. Null says "not applicable".
    /// </summary>
    [Fact]
    public async Task WithNothingTracked_TheDerivedFiguresAreNull_NotZero()
    {
        _flights.CountActiveTrackedFlightsAsync(
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _flights.CountTrackedFlightsByStoredSyncModeAsync(Arg.Any<CancellationToken>()).Returns([]);
        _flights.CountStatusEventsBySourceAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateHandler().Handle(new GetFlightDataUsageQuery(), CancellationToken.None);

        Assert.Null(result.Derived.CostUsdPerActiveTrackedFlight);
        Assert.Null(result.Derived.CostUsdPerRecordedEvent);
        // The spend is still reported — money was spent even with nothing tracked now, which is
        // itself the finding an operator wants to see.
        Assert.Equal(2.00m, result.TotalCostUsd);
    }

    /// <summary>
    /// Modes come back in the enum's declaration order with the never-planned group last, so the
    /// panel renders a stable sequence rather than whatever order GROUP BY happened to return.
    /// </summary>
    [Fact]
    public async Task ModesAreOrderedByTheEnum_WithNeverPlannedLast()
    {
        _flights.CountActiveTrackedFlightsAsync(
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(9);
        _flights.CountTrackedFlightsByStoredSyncModeAsync(Arg.Any<CancellationToken>())
            .Returns([(null, 1), (FlightSyncMode.Stopped, 4), (FlightSyncMode.Fast, 2), (FlightSyncMode.Live, 2)]);
        _flights.CountStatusEventsBySourceAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateHandler().Handle(new GetFlightDataUsageQuery(), CancellationToken.None);

        Assert.Equal(
            ["Fast", "Live", "Stopped", null],
            result.ByStoredSyncMode.Select(m => m.Mode));
    }

    /// <summary>Event yield leads with the busiest source, so the one that spent the money is first.</summary>
    [Fact]
    public async Task EventYieldLeadsWithTheBusiestSource()
    {
        _flights.CountActiveTrackedFlightsAsync(
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _flights.CountTrackedFlightsByStoredSyncModeAsync(Arg.Any<CancellationToken>()).Returns([]);
        _flights.CountStatusEventsBySourceAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([("manual", 1, 1), ("AeroAPI", 12, 2)]);

        var result = await CreateHandler().Handle(new GetFlightDataUsageQuery(), CancellationToken.None);

        Assert.Equal(["AeroAPI", "manual"], result.EventYield.Select(y => y.Source));
        Assert.Equal(13, result.TotalEventsRecorded);
    }

    /// <summary>
    /// The event window is the <em>resolved</em> range, not "everything" — a report that counted
    /// every event ever recorded against one month's spend would be wrong in a way nobody could see
    /// from the numbers themselves.
    /// </summary>
    [Fact]
    public async Task EventsAreCountedOverTheResolvedWindow()
    {
        StubLocalReads();

        var result = await CreateHandler().Handle(new GetFlightDataUsageQuery(), CancellationToken.None);

        await _flights.Received(1).CountStatusEventsBySourceAsync(
            result.RangeStartUtc.UtcDateTime,
            result.RangeEndUtc.UtcDateTime,
            Arg.Any<CancellationToken>());
    }

    private void StubLocalReads()
    {
        _flights.CountActiveTrackedFlightsAsync(
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(1);
        _flights.CountTrackedFlightsByStoredSyncModeAsync(Arg.Any<CancellationToken>()).Returns([]);
        _flights.CountStatusEventsBySourceAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns([]);
    }
}
