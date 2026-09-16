using System.Collections.Concurrent;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.API.Tests.HostedServices;

public class FlightStatusSyncBackgroundServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly FlightSyncHeartbeat _heartbeat = new();
    private int _mediatorScopes;

    private FlightStatusSyncBackgroundService Service(
        FlightSyncOptions? options = null, ILogger<FlightStatusSyncBackgroundService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _flights);
        // Counts how many scopes resolved a mediator: one per flight.
        services.AddScoped(_ => { _mediatorScopes++; return _mediator; });

        return new FlightStatusSyncBackgroundService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? new FlightSyncOptions()),
            _clock,
            logger ?? NullLogger<FlightStatusSyncBackgroundService>.Instance,
            _heartbeat);
    }

    /// <summary>
    /// Records every log call, so a test can assert a warning was actually emitted. A
    /// <see cref="ConcurrentQueue{T}"/> rather than a plain list — a run that overruns the loop
    /// interval has the next one start immediately (by design), so the background loop can still be
    /// writing here when the test thread reads <see cref="Entries"/> after only the first success.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private void DueIds(params int[] ids) =>
        _flights.GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>()).Returns(ids.ToList());

    private List<SyncFlightStatusCommand> Sent() => _mediator.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<SyncFlightStatusCommand>().ToList();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 5 s.");
            await Task.Delay(10);
        }
    }

    // ── RunOnceAsync ──

    [Fact]
    public async Task RunOnce_SendsAScheduledSyncForEachDueFlight_InTheRepositorysOrder()
    {
        DueIds(3, 1, 2);

        var result = await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { 3, 1, 2 }, Sent().Select(c => c.FlightId));
        Assert.All(Sent(), c =>
        {
            Assert.Equal(SyncTrigger.Scheduled, c.Trigger);
            Assert.Null(c.TravelerId);
        });
        Assert.Equal(new FlightSyncRunResult(3, 0), result);
    }

    [Fact]
    public async Task RunOnce_NoDueFlights_SendsNothing()
    {
        DueIds();

        Assert.Equal(new FlightSyncRunResult(0, 0), await Service().RunOnceAsync(CancellationToken.None));
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task RunOnce_AFailingFlight_IsCounted_AndTheRestStillSync()
    {
        DueIds(1, 2, 3);
        _mediator.Send(Arg.Is<SyncFlightStatusCommand>(c => c.FlightId == 1), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));

        var result = await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(new FlightSyncRunResult(3, 1), result);
        Assert.Equal(new[] { 1, 2, 3 }, Sent().Select(c => c.FlightId));
    }

    [Fact]
    public async Task RunOnce_EachFlightSyncsInItsOwnScope()
    {
        // A shared DbContext would let one flight's failed, half-applied changes be saved by the next.
        DueIds(1, 2, 3);

        await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, _mediatorScopes);
    }

    // ── ExecuteAsync ──

    [Fact]
    public async Task Disabled_NeverLooksForFlights_AndTheHeartbeatStaysNeverRun()
    {
        var service = Service(new FlightSyncOptions { Enabled = false });

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        await _flights.DidNotReceive().GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());
        Assert.Equal(FlightSyncOutcome.NeverRun, _heartbeat.Current.LastOutcome);
    }

    [Fact]
    public async Task Enabled_RunsAtOnce_ThenOnEveryLoopInterval()
    {
        DueIds();
        var service = Service(new FlightSyncOptions { LoopInterval = TimeSpan.FromSeconds(30) }); // non-default

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => _heartbeat.Current.LastOutcome == FlightSyncOutcome.Success);
            Assert.Equal(Now.AddSeconds(30), _heartbeat.Current.NextRunDueUtc);
            await _flights.Received(1).GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());

            _clock.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(100);
            await _flights.Received(1).GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());

            _clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntil(() => _flights.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IFlightRepository.GetDueTrackedFlightIdsAsync)) == 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ARun_RecordsHowLongItTook()
    {
        // The stub advances the fake clock while the run is in flight, so the recorded duration is
        // exactly what the clock moved — no wall-clock timing in the assertion.
        DueIds(1);
        _mediator
            .Send(Arg.Any<SyncFlightStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _clock.Advance(TimeSpan.FromSeconds(20)); return true; });

        var service = Service();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => _heartbeat.Current.LastOutcome == FlightSyncOutcome.Success);
            Assert.Equal(TimeSpan.FromSeconds(20), _heartbeat.Current.LastRunDuration);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ARunLongerThanTheLoopInterval_LogsAWarning()
    {
        // Advances the clock past LoopInterval exactly once. If every Send kept advancing it, the
        // loop would never go back to blocking on WaitForNextTickAsync — it would free-run on the
        // fake clock and starve the thread pool, making WaitUntil's own polling an unreliable race
        // rather than a deterministic wait (this is what made the test flaky before: ~16% of runs
        // timed out, never observing Success within WaitUntil's real-time deadline).
        DueIds(1);
        var advanced = false;
        _mediator
            .Send(Arg.Any<SyncFlightStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (!advanced)
                {
                    advanced = true;
                    _clock.Advance(TimeSpan.FromMinutes(5));
                }
                return true;
            });

        var logger = new CapturingLogger<FlightStatusSyncBackgroundService>();
        var service = Service(logger: logger);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => _heartbeat.Current.LastOutcome == FlightSyncOutcome.Success);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("longer than"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ── #344 end to end: real handler and planner, fake clock and repository ──

    private static Flight WithId(Flight flight, int id)
    {
        typeof(BaseIntEntity).GetProperty(nameof(BaseIntEntity.Id))!.SetValue(flight, id);
        return flight;
    }

    /// <summary>
    /// Three tracked flights, all due now, run through the real handler and planner once a minute for
    /// ten minutes. TK1 departs in 3 h (Fast), TK2 in 30 h (Live) and TK3 in 20 days (Schedule).
    /// The intervals are non-default (#308): Fast every 3 min and Live every 20 min, with the Fast
    /// window left at 4 h.
    /// </summary>
    private async Task<(IFlightStatusProvider Provider, Flight[] Flights, int Failed)> RunThreeFlightsForTenMinutesAsync(
        ProviderBudgetState budgetState)
    {
        var options = new FlightSyncOptions { NearDepartureInterval = TimeSpan.FromMinutes(3), Interval = TimeSpan.FromMinutes(20) };
        var fast = WithId(Flight.Create(1, "TK1", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(3), Now.AddHours(6)), 1);
        var live = WithId(Flight.Create(1, "TK2", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(30), Now.AddHours(33)), 2);
        var schedule = WithId(Flight.Create(1, "TK3", 10, 20, DateOnly.FromDateTime(Now.AddDays(20)), Now.AddDays(20), Now.AddDays(20).AddHours(3)), 3);
        var all = new[] { fast, live, schedule };
        foreach (var flight in all)
            flight.ApplySyncPlan(FlightSyncMode.Live, Now); // due now, as the migration backfill leaves them

        _flights.GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>()).Returns(_ => all
            .Where(f => f.NextSyncDueUtc <= _clock.GetUtcNow().UtcDateTime)
            .OrderBy(f => f.NextSyncDueUtc)
            .Select(f => f.Id)
            .ToList());
        _flights.GetFlightByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ci => all.Single(f => f.Id == ci.ArgAt<int>(0)));
        _flights.IsFlightTrackedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var provider = Substitute.For<IFlightStatusProvider>();
        provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var window = Substitute.For<IFlightStatusWindowOptions>();
        window.StatusWindowFutureDays.Returns(2);
        var budget = Substitute.For<IProviderBudgetGuard>();
        budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(budgetState);
        var handler = new SyncFlightStatusCommandHandler(_flights, provider,
            new FlightStatusApplier(_flights, NullLogger<FlightStatusApplier>.Instance), budget,
            Substitute.For<ICacheService>(), options, window, _clock,
            NullLogger<SyncFlightStatusCommandHandler>.Instance);
        _mediator.Send(Arg.Any<SyncFlightStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.ArgAt<SyncFlightStatusCommand>(0), CancellationToken.None).GetAwaiter().GetResult());

        var service = Service(options);
        var failed = 0;
        for (var minute = 0; minute < 10; minute++)
        {
            failed += (await service.RunOnceAsync(CancellationToken.None)).Failed;
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        return (provider, all, failed);
    }

    [Fact]
    public async Task OverTenMinutes_AFlightNearDeparture_DoesNotSpeedUpTheOthers()
    {
        var (provider, _, _) = await RunThreeFlightsForTenMinutesAsync(ProviderBudgetState.Normal);

        await provider.Received(4).GetStatusAsync("TK1", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()); // minutes 0, 3, 6, 9
        await provider.Received(1).GetStatusAsync("TK2", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()); // minute 0 only
        await provider.DidNotReceive().GetStatusAsync("TK3", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        // Every call the loop makes is a scheduled poll (#375).
        await provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), StatusCallKind.Interactive, Arg.Any<CancellationToken>());
        Assert.Single(Sent(), c => c.FlightId == 3); // planned once, next due when Live starts
    }

    [Fact]
    public async Task OverTenMinutes_BudgetExhausted_OnlyTheFlightNearDepartureIsCalled()
    {
        var (provider, flights, failed) = await RunThreeFlightsForTenMinutesAsync(ProviderBudgetState.Exhausted);

        await provider.Received(4).GetStatusAsync("TK1", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()); // Fast keeps its interval
        await provider.DidNotReceive().GetStatusAsync("TK2", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        await provider.DidNotReceive().GetStatusAsync("TK3", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        // TK2 was checked at minute 0 without a call, and waits twice the Live interval.
        Assert.Null(flights[1].LastSyncedUtc);
        Assert.Equal(Now.AddMinutes(40), flights[1].NextSyncDueUtc);
        Assert.Equal(0, failed); // held back, not failed
    }
}
