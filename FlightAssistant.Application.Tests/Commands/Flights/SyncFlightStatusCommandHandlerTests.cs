using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using FlightAssistant.Domain.Flights.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class SyncFlightStatusCommandHandlerTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IFlightStatusProvider _provider = Substitute.For<IFlightStatusProvider>();
    private readonly IProviderBudgetGuard _budget = Substitute.For<IProviderBudgetGuard>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly SyncFlightStatusCommandHandler _handler;

    // Non-default values, so the handler can't pass with the planner's defaults hardcoded.
    private sealed class SyncOptions : IFlightSyncOptions
    {
        public TimeSpan NearDepartureInterval => TimeSpan.FromMinutes(3);
        public TimeSpan Interval => TimeSpan.FromMinutes(7);
        public TimeSpan ScheduleCheckInterval => TimeSpan.FromHours(6);
        public TimeSpan FastWindowBeforeDeparture => TimeSpan.FromHours(3);
        public TimeSpan StopAfterArrival => TimeSpan.FromMinutes(20);
        public TimeSpan StopWithoutArrivalAfter => TimeSpan.FromHours(12);
        public TimeSpan ManualSyncCooldown { get; init; } = TimeSpan.FromMinutes(4);
        public int MaxActiveTrackedFlightsPerTraveler => 3;
    }

    private sealed class WindowOptions : IFlightStatusWindowOptions
    {
        public int StatusWindowFutureDays => 1;
    }

    /// <summary>Records every log call made through it, so a test can assert a warning was
    /// actually emitted rather than an unrecognised status silently falling back to UNK unnoticed
    /// (mirrors the equivalent helper in AeroApiFlightStatusProviderTests).</summary>
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

    /// <summary>A dictionary cache that ignores expiry but records the last one set per key, and
    /// can be made to fail, counting every access either way.</summary>
    private sealed class FakeCache : ICacheService
    {
        public Dictionary<string, object?> Entries { get; } = [];
        public Dictionary<string, TimeSpan?> Expiries { get; } = []; // written only by SetAsync
        public bool FailReads { get; init; }
        public bool FailWrites { get; init; }
        public int Accesses { get; private set; }

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            Accesses++;
            if (FailReads) return Task.FromException<T?>(new InvalidOperationException("cache down"));
            return Task.FromResult(Entries.TryGetValue(key, out var value) ? (T?)value : default);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            Accesses++;
            if (FailWrites) return Task.FromException(new InvalidOperationException("cache down"));
            Entries[key] = value;
            Expiries[key] = absoluteExpire;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            Accesses++;
            if (FailWrites) return Task.FromException(new InvalidOperationException("cache down"));
            Entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private readonly FakeCache _cache = new();

    private static string MarkerKey(Flight flight) => $"flight-sync:traveler-attempt:{flight.Id}";

    private static Flight Departing(DateTime departureUtc) =>
        Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(departureUtc), departureUtc, departureUtc.AddHours(8));

    private static FlightStatusResult Status(string code = "DEP", DateTime? effectiveAtUtc = null, DateTime? estimatedDepartureUtc = null) => new(
        StatusCode: code,
        EffectiveAtUtc: effectiveAtUtc ?? Now,
        EstimatedDepartureUtc: estimatedDepartureUtc,
        EstimatedArrivalUtc: null,
        ActualDepartureUtc: null,
        ActualArrivalUtc: null,
        Gate: "A1",
        Terminal: "T5",
        BaggageClaim: null,
        EquipmentIcao: "B789",
        ExternalId: "ext-1",
        RawPayloadJson: null);

    private SyncFlightStatusCommandHandler Handler(
        ILogger<SyncFlightStatusCommandHandler>? logger = null, IFlightSyncOptions? syncOptions = null,
        ICacheService? cache = null) =>
        new(_flights, _provider, _budget, cache ?? _cache, syncOptions ?? new SyncOptions(), new WindowOptions(), _clock,
            logger ?? NullLogger<SyncFlightStatusCommandHandler>.Instance);

    public SyncFlightStatusCommandHandlerTests()
    {
        _handler = Handler();
        _provider.ProviderName.Returns("TestProvider");
        _flights.IsFlightTrackedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _flights.GetFlightStatusIdByCodeAsync("DEP", Arg.Any<CancellationToken>()).Returns(2);
        _flights.GetFlightStatusIdByCodeAsync("CAN", Arg.Any<CancellationToken>()).Returns(5);
        _flights.GetFlightStatusIdByCodeAsync("SCH", Arg.Any<CancellationToken>()).Returns(1);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
    }

    private Flight Given(Flight flight)
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);
        return flight;
    }

    private Task<bool> Sync(SyncTrigger trigger = SyncTrigger.Scheduled, Guid? travelerId = null) =>
        _handler.Handle(new SyncFlightStatusCommand(travelerId, 1, trigger), CancellationToken.None);

    private void BudgetIs(ProviderBudgetState state) =>
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(state);

    // ── Fast / Live ──

    [Fact]
    public async Task Fast_NewStatus_RecordsEvent_MarksSynced_NextSyncAfterTheFastInterval()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).Returns(Status());

        Assert.True(await Sync());

        await _flights.Received(1).AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Live_NextSyncAfterTheLiveInterval()
    {
        var flight = Given(Departing(Now.AddHours(10)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync();

        Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(7), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task ProviderReturnsNothing_ReturnsFalse_ButRecordsTheSyncAndSaves()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        Assert.False(await Sync());

        Assert.Equal(Now, flight.LastSyncedUtc);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DuplicateStatus_ReturnsFalse_AddsNoEvent_ButRecordsTheSync()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).Returns(Status());
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        Assert.False(await Sync());

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusUpdateMovesTheDeparture_NextSyncIsPlannedFromTheNewTimes()
    {
        // Live before the call; the estimate puts departure inside the 3 h Fast window.
        var flight = Given(Departing(Now.AddHours(10)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status("DLY", estimatedDepartureUtc: Now.AddHours(2)));
        _flights.GetFlightStatusIdByCodeAsync("DLY", Arg.Any<CancellationToken>()).Returns(4);

        await Sync();

        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Cancelled_SetsCancelledAtUtc_AndALaterOtherStatusClearsIt()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).Returns(Status("CAN"));

        await Sync();
        Assert.Equal(Now, flight.CancelledAtUtc);

        _clock.Advance(TimeSpan.FromMinutes(3));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status("SCH", effectiveAtUtc: Now.AddMinutes(3)));

        await Sync();
        Assert.Null(flight.CancelledAtUtc);
    }

    // ── none / Stopped / Schedule ──

    [Fact]
    public async Task Untracked_MakesNoCall_ClearsThePlan()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now);
        _flights.IsFlightTrackedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.False(await Sync(SyncTrigger.Admin));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Null(flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
        Assert.Null(flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static Flight ArrivedAnHourAgo()
    {
        var flight = Departing(Now.AddHours(-9));
        flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-9), Now.AddHours(-1), null, Now.AddHours(-1));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now);
        return flight;
    }

    [Fact]
    public async Task Stopped_ScheduledTrigger_MakesNoCall_StoresStopped()
    {
        var flight = Given(ArrivedAnHourAgo());

        Assert.False(await Sync(SyncTrigger.Scheduled));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Equal(FlightSyncMode.Stopped, flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
        Assert.Null(flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SyncTrigger.Admin)]
    [InlineData(SyncTrigger.Traveler)]
    public async Task Stopped_AskedForByAPerson_StillCallsTheProvider(SyncTrigger trigger)
    {
        var flight = Given(ArrivedAnHourAgo());
        Guid? travelerId = trigger == SyncTrigger.Traveler ? Guid.NewGuid() : null;
        if (travelerId is { } id)
            _flights.GetTravelerFlightAsync(id, 1, Arg.Any<CancellationToken>()).Returns(TravelerFlight.Create(id, 1));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(trigger, travelerId);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Stopped, flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task ArrivalRecorded_StopsTheFlight_StopAfterArrivalLater()
    {
        // #345: the reported arrival stops the loop, not the no-arrival fallback (scheduled arrival
        // is 6 h away here, so that fallback can't be what stops it).
        var flight = Given(Departing(Now.AddHours(-2)));
        var arrived = Now.AddMinutes(-5);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status("ARR") with { ActualArrivalUtc = arrived });
        _flights.GetFlightStatusIdByCodeAsync("ARR", Arg.Any<CancellationToken>()).Returns(3);

        Assert.True(await Sync());
        Assert.Equal(arrived, flight.ActualArrivalUtc);
        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);

        _clock.SetUtcNow(new DateTimeOffset(arrived + new SyncOptions().StopAfterArrival));
        Assert.False(await Sync());

        await _provider.Received(1).GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Equal(FlightSyncMode.Stopped, flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task ScheduledSync_OfAFlightNoLongerDue_MakesNoCallAndSavesNothing()
    {
        // The loop lists due flights, then syncs them 12 s apart; a manual sync in between has
        // already moved this one's next sync on.
        var flight = Given(Departing(Now.AddHours(1)));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now.AddMinutes(1));

        Assert.False(await Sync(SyncTrigger.Scheduled));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(Now.AddMinutes(1), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task AdminSync_OfAFlightNotYetDue_StillCallsTheProvider()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now.AddMinutes(1));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Admin);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    // ── Traveler cooldown (#369) ──

    [Fact]
    public async Task TravelerSync_WithinTheCooldown_MakesNoCallAndSavesNothing()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.MarkSynced(Now.AddMinutes(-3)); // the cooldown is 4 min

        Assert.False(await Sync(SyncTrigger.Traveler));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(Now.AddMinutes(-3), flight.LastSyncedUtc);
    }

    [Theory]
    [InlineData(4)]  // exactly the cooldown ago: no longer within it
    [InlineData(10)]
    public async Task TravelerSync_CooldownOver_CallsTheProvider(int minutesSinceLastSync)
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.MarkSynced(Now.AddMinutes(-minutesSinceLastSync));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Traveler);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TravelerSync_NeverSynced_CallsTheProvider()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Traveler);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminSync_WithinTheTravelerCooldown_StillCallsTheProvider()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.MarkSynced(Now.AddMinutes(-1));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Admin);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TravelerSync_CooldownZero_IsOff()
    {
        var handler = Handler(syncOptions: new SyncOptions { ManualSyncCooldown = TimeSpan.Zero });
        var flight = Given(Departing(Now.AddHours(1)));
        flight.MarkSynced(Now);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new SyncFlightStatusCommand(null, 1, SyncTrigger.Traveler), CancellationToken.None);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    // ── Traveler-attempt marker (#369): the cooldown starts when the call is attempted ──

    [Fact]
    public async Task TravelerSync_ProviderThrows_ARetryMakesNoCall()
    {
        Given(Departing(Now.AddHours(1)));
        // Fails once, then answers, so a retry that got through shows as a second call.
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns<FlightStatusResult?>(_ => throw new DomainException("AeroAPI unavailable", 502), _ => null);

        await Assert.ThrowsAsync<DomainException>(() => Sync(SyncTrigger.Traveler));
        Assert.False(await Sync(SyncTrigger.Traveler));

        await _provider.Received(1).GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TravelerSync_WhileAnotherAttemptIsInFlight_MakesNoCallAndSavesNothing()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _cache.Entries[MarkerKey(flight)] = Now.AddSeconds(-5).ToString("O");

        Assert.False(await Sync(SyncTrigger.Traveler));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Null(flight.LastSyncedUtc);
    }

    [Fact]
    public async Task TravelerSync_SetsTheMarkerBeforeTheCall_ExpiringAfterTheCooldown()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        var markedBeforeTheCall = false;
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns<FlightStatusResult?>(_ =>
            {
                markedBeforeTheCall = _cache.Entries.ContainsKey(MarkerKey(flight));
                return null;
            });

        await Sync(SyncTrigger.Traveler);

        Assert.True(markedBeforeTheCall);
        Assert.Equal(Now.ToString("O"), _cache.Entries[MarkerKey(flight)]);
        Assert.Equal(TimeSpan.FromMinutes(4), _cache.Expiries[MarkerKey(flight)]);
    }

    [Theory]
    [InlineData(SyncTrigger.Admin)]
    [InlineData(SyncTrigger.Scheduled)]
    public async Task AdminAndScheduledSyncs_IgnoreTheMarker_AndSetNone(SyncTrigger trigger)
    {
        var flight = Given(Departing(Now.AddHours(1))); // never planned, so due for the loop
        _cache.Entries[MarkerKey(flight)] = Now.ToString("O");
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(trigger);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Empty(_cache.Expiries);
    }

    [Fact]
    public async Task TravelerSync_CooldownZero_NeverTouchesTheCache()
    {
        var cache = new FakeCache { FailReads = true, FailWrites = true };
        var handler = Handler(syncOptions: new SyncOptions { ManualSyncCooldown = TimeSpan.Zero }, cache: cache);
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new SyncFlightStatusCommand(null, 1, SyncTrigger.Traveler), CancellationToken.None);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, cache.Accesses);
    }

    [Fact]
    public async Task TravelerSync_MarkerReadFails_StillCallsTheProvider_AndLogsAWarning()
    {
        var logger = new CapturingLogger<SyncFlightStatusCommandHandler>();
        var handler = Handler(logger, cache: new FakeCache { FailReads = true });
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new SyncFlightStatusCommand(null, 1, SyncTrigger.Traveler), CancellationToken.None);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task TravelerSync_MarkerWriteFails_StillCallsTheProvider_AndLogsAWarning()
    {
        var logger = new CapturingLogger<SyncFlightStatusCommandHandler>();
        var handler = Handler(logger, cache: new FakeCache { FailWrites = true });
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new SyncFlightStatusCommand(null, 1, SyncTrigger.Traveler), CancellationToken.None);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task TravelerSync_ScheduleMode_MakesNoCall_AndSetsNoMarker()
    {
        Given(Departing(Now.AddDays(30)));

        Assert.False(await Sync(SyncTrigger.Traveler));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Empty(_cache.Expiries);
    }

    [Theory]
    [InlineData(SyncTrigger.Scheduled)]
    [InlineData(SyncTrigger.Admin)]
    public async Task Schedule_MakesNoCall_NextSyncAtTheStartOfLive(SyncTrigger trigger)
    {
        var departure = Now.AddDays(5);
        var flight = Given(Departing(departure));

        Assert.False(await Sync(trigger));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Equal(FlightSyncMode.Schedule, flight.SyncMode);
        Assert.Equal(departure.AddDays(-1), flight.NextSyncDueUtc); // StatusWindowFutureDays = 1
        Assert.Null(flight.LastSyncedUtc);
    }

    // ── Failures ──

    private void ProviderThrows(Exception ex) =>
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns<FlightStatusResult?>(_ => throw ex);

    [Fact]
    public async Task ScheduledSync_ProviderThrows_Propagates_AndMovesTheNextSyncOneIntervalOn()
    {
        // A failed call can still be billed; retrying on every loop would cost a call a minute.
        var flight = Given(Departing(Now.AddHours(1)));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now);
        ProviderThrows(new DomainException("AeroAPI unavailable", 502));

        await Assert.ThrowsAsync<DomainException>(() => Sync(SyncTrigger.Scheduled));

        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
        Assert.Null(flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SyncTrigger.Admin)]
    [InlineData(SyncTrigger.Traveler)]
    public async Task PersonSync_ProviderThrows_PropagatesAndSavesNothing(SyncTrigger trigger)
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now);
        Guid? travelerId = trigger == SyncTrigger.Traveler ? Guid.NewGuid() : null;
        if (travelerId is { } id)
            _flights.GetTravelerFlightAsync(id, 1, Arg.Any<CancellationToken>()).Returns(TravelerFlight.Create(id, 1));
        ProviderThrows(new DomainException("AeroAPI unavailable", 502));

        await Assert.ThrowsAsync<DomainException>(() => Sync(trigger, travelerId));

        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Null(flight.LastSyncedUtc);
        Assert.Equal(Now, flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task ScheduledSync_ProviderThrows_AndTheSaveFails_StillThrowsTheProviderError()
    {
        Given(Departing(Now.AddHours(1)));
        ProviderThrows(new DomainException("AeroAPI unavailable", 502));
        _flights.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("database down")));

        var ex = await Assert.ThrowsAsync<DomainException>(() => Sync(SyncTrigger.Scheduled));

        Assert.Equal(502, ex.StatusCode);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduledSync_CancelledByTheToken_SavesNothing()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ProviderThrows(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _handler.Handle(new SyncFlightStatusCommand(null, 1, SyncTrigger.Scheduled), cts.Token));

        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task FlightNotFound_Throws404()
    {
        _flights.GetFlightByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new SyncFlightStatusCommand(null, 99, SyncTrigger.Admin), CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task WithTravelerId_NotTracking_Throws403()
    {
        var travelerId = Guid.NewGuid();
        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Sync(SyncTrigger.Traveler, travelerId));

        Assert.Equal(403, ex.StatusCode);
        await _flights.DidNotReceive().GetFlightByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Unrecognised status code — falls back to UNK rather than aborting (#281)
    // ------------------------------------------------------------------

    /// <summary>
    /// A code the repository can't resolve reaches this far only if a provider has started
    /// emitting something outside its own "UNK" fallback mapping — see
    /// AeroApiFlightStatusProvider.MapStatusCode. Aborting the whole sync would both lose this
    /// flight's status update and discard the AeroAPI call already spent fetching it, so the
    /// handler degrades to the seeded "UNK" row instead and logs a warning naming the code.
    /// </summary>
    [Fact]
    public async Task UnrecognisedStatusCode_FallsBackToUnk_AndLogsWarning_RatherThanThrowing()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status() with { StatusCode = "WEIRD" });
        _flights.GetFlightStatusIdByCodeAsync("WEIRD", Arg.Any<CancellationToken>()).ReturnsNull();
        _flights.GetFlightStatusIdByCodeAsync("UNK", Arg.Any<CancellationToken>()).Returns(7);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var logger = new CapturingLogger<SyncFlightStatusCommandHandler>();
        var handler = Handler(logger);

        var result = await handler.Handle(new SyncFlightStatusCommand(null, 1, SyncTrigger.Scheduled), CancellationToken.None);

        Assert.True(result);
        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e => e.StatusId == 7), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("WEIRD"));
    }

    /// <summary>
    /// The one case that should still be loud: if "UNK" itself can't be resolved, the reference
    /// table is misconfigured (a genuine deployment error), not a provider surprise, so this
    /// throws rather than swallowing the sync. The billed call is already spent, so the loop
    /// defers the flight one interval like any other failure.
    /// </summary>
    [Fact]
    public async Task UnrecognisedStatusCode_Throws_WhenUnkItselfIsNotSeeded_AndDefersTheNextSync()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status() with { StatusCode = "WEIRD" });
        _flights.GetFlightStatusIdByCodeAsync("WEIRD", Arg.Any<CancellationToken>()).ReturnsNull();
        _flights.GetFlightStatusIdByCodeAsync("UNK", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Sync(SyncTrigger.Scheduled));
        Assert.Equal(404, ex.StatusCode);

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Null(flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task TwoDistinctStatuses_BothStored()
    {
        // First call: not a dedup
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status());
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var first = await Sync();

        // Second call, once the flight is due again: different effective time → different hash → also not a dedup
        _clock.Advance(new SyncOptions().NearDepartureInterval);
        var laterStatus = Status() with { EffectiveAtUtc = Now.AddMinutes(30) };
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(laterStatus);

        var second = await Sync();

        Assert.True(first);
        Assert.True(second);
        await _flights.Received(2).AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Retry intent (#375) ──

    [Fact]
    public async Task ScheduledSync_CallsTheProvider_AsAScheduledPoll()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Scheduled);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, StatusCallKind.ScheduledPoll, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SyncTrigger.Admin)]
    [InlineData(SyncTrigger.Traveler)]
    public async Task PersonSync_CallsTheProvider_AsInteractive(SyncTrigger trigger)
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(trigger);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, StatusCallKind.Interactive, Arg.Any<CancellationToken>());
    }

    // ── Budget guard (#303) ──

    [Fact]
    public async Task Live_PastTheSoftLimit_CallsTheProvider_NextSyncAfterTwiceTheLiveInterval()
    {
        var flight = Given(Departing(Now.AddHours(10)));
        BudgetIs(ProviderBudgetState.SoftLimit);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync();

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, StatusCallKind.ScheduledPoll, Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(14), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Live_PastTheSoftLimit_AFailedScheduledSync_WaitsTwiceTheLiveInterval()
    {
        var flight = Given(Departing(Now.AddHours(10)));
        BudgetIs(ProviderBudgetState.SoftLimit);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns<FlightStatusResult?>(_ => throw new DomainException("AeroAPI unavailable", 502));

        await Assert.ThrowsAsync<DomainException>(() => Sync());

        Assert.Null(flight.LastSyncedUtc);
        Assert.Equal(Now.AddMinutes(14), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Live_BudgetExhausted_ScheduledSync_MakesNoCall_NextSyncAfterTwiceTheLiveInterval()
    {
        var flight = Given(Departing(Now.AddHours(10)));
        BudgetIs(ProviderBudgetState.Exhausted);

        Assert.False(await Sync());

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Null(flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(14), flight.NextSyncDueUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Live_BudgetExhausted_ScheduledSync_IsDueAgainNoLaterThanFastStarts()
    {
        // Fast starts in 10 min, before twice the Live interval (14 min).
        var flight = Given(Departing(Now.AddHours(3).AddMinutes(10)));
        BudgetIs(ProviderBudgetState.Exhausted);

        await Sync();

        Assert.Equal(Now.AddMinutes(10), flight.NextSyncDueUtc);
    }

    [Theory]
    [InlineData(SyncTrigger.Admin)]
    [InlineData(SyncTrigger.Traveler)]
    public async Task Live_BudgetExhausted_PersonSync_Throws503_WithoutACallOrAMarker(SyncTrigger trigger)
    {
        var flight = Given(Departing(Now.AddHours(10)));
        BudgetIs(ProviderBudgetState.Exhausted);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Sync(trigger));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("budget", ex.Message);
        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.False(_cache.Entries.ContainsKey(MarkerKey(flight)));
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stopped_BudgetExhausted_PersonSync_Throws503_WithoutACall()
    {
        Given(ArrivedAnHourAgo());
        BudgetIs(ProviderBudgetState.Exhausted);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Sync(SyncTrigger.Admin));

        Assert.Equal(503, ex.StatusCode);
        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fast_BudgetExhausted_StillCallsTheProvider_WithoutReadingTheBudget()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        BudgetIs(ProviderBudgetState.Exhausted);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync();

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, StatusCallKind.ScheduledPoll, Arg.Any<CancellationToken>());
        await _budget.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Schedule_NeverReadsTheBudget()
    {
        Given(Departing(Now.AddDays(5)));

        await Sync();

        await _budget.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TravelerSync_WhileTheFirstWaitsOnTheBudgetRead_MakesNoSecondCall()
    {
        // The budget read can wait on the AeroAPI throttle, so the attempt marker must already be set (#369).
        Given(Departing(Now.AddHours(10)));
        var budgetRead = new TaskCompletionSource<ProviderBudgetState>(TaskCreationOptions.RunContinuationsAsynchronously);
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(budgetRead.Task);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var first = Sync(SyncTrigger.Traveler);
        var second = Sync(SyncTrigger.Traveler);
        budgetRead.SetResult(ProviderBudgetState.Normal);
        await Task.WhenAll(first, second);

        await _provider.Received(1).GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    // ── A status the flight already has (#351) ──

    /// <summary>The provider reports <paramref name="reported"/> for a status already stored for the flight,
    /// whose latest event has the given details.</summary>
    private Flight GivenKnownStatus(FlightStatusResult reported, string? gate = "A1", string? terminal = "T5",
        string? baggageClaim = null, DateTime? departureUtc = null)
    {
        var flight = Given(Departing(departureUtc ?? Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(reported);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>())
            .Returns(FlightStatusEvent.Create(flight.Id, 2, Now, "TestProvider", "ext-1", gate, terminal, baggageClaim));
        return flight;
    }

    [Fact]
    public async Task NewStatus_StillRaisesFlightStatusChanged()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status());

        Assert.True(await Sync());

        Assert.Single(flight.DomainEvents, e => e is FlightStatusChanged);
    }

    [Fact]
    public async Task KnownStatus_ANewGate_RecordsADetailChange_WithNoPush()
    {
        var flight = GivenKnownStatus(Status() with { Gate = "B7" });

        Assert.True(await Sync());

        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e =>
                e.Gate == "B7" && e.Terminal == "T5" && e.BaggageClaim == null
                && e.StatusId == 2 && e.EffectiveAtUtc == Now && e.Source == "TestProvider"
                && e.DedupHash == FlightStatusEvent.ComputeDetailChangeHash(flight.Id, 2, Now, "TestProvider", "ext-1", 0, "B7", "T5", null)),
            Arg.Any<CancellationToken>());
        Assert.DoesNotContain(flight.DomainEvents, e => e is FlightStatusChanged);
        Assert.Equal(Now, flight.LastSyncedUtc);
    }

    [Fact]
    public async Task KnownStatus_ABaggageClaimReportedLater_IsRecorded_KeepingDetailsNoLongerReported()
    {
        var flight = GivenKnownStatus(Status() with { Gate = null, BaggageClaim = "7" });

        Assert.True(await Sync());

        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e => e.Gate == "A1" && e.Terminal == "T5" && e.BaggageClaim == "7"),
            Arg.Any<CancellationToken>());
        Assert.DoesNotContain(flight.DomainEvents, e => e is FlightStatusChanged);
    }

    [Fact]
    public async Task KnownStatus_ADetailNoLongerReported_RecordsNothing()
    {
        GivenKnownStatus(Status() with { Gate = null });

        Assert.False(await Sync());

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KnownStatus_AnIdenticalResponse_RecordsNothing_AndLeavesTheFlightUnchanged()
    {
        var flight = GivenKnownStatus(Status() with { EquipmentIcao = null });
        var updatedBefore = flight.UpdatedUtc;

        Assert.False(await Sync());

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        Assert.Equal(updatedBefore, flight.UpdatedUtc);
        Assert.Empty(flight.DomainEvents);
    }

    [Fact]
    public async Task KnownStatus_ANewEstimatedArrival_UpdatesTheFlight_WithNoEventOrPush()
    {
        var flight = GivenKnownStatus(Status() with { EstimatedArrivalUtc = Now.AddHours(9) });

        Assert.False(await Sync());

        Assert.Equal(Now.AddHours(9), flight.EstimatedArrivalUtc);
        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        Assert.Empty(flight.DomainEvents);
    }

    [Fact]
    public async Task KnownStatus_ANewEstimatedDeparture_IsPlannedFromTheNewTime()
    {
        // Live before the call; the estimate moves departure inside the 3 h Fast window, with no new status.
        var flight = GivenKnownStatus(Status() with { EstimatedDepartureUtc = Now.AddHours(2) }, departureUtc: Now.AddHours(10));

        await Sync();

        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }
}
