using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;

namespace FlightAssistant.Application.Tests.FlightSync;

/// <summary>Spec §1 and §1.1. Every option is a non-default value, so a hardcoded 4 h / 2 days /
/// 30 min / 24 h / 2 min / 10 min / 1 day can't pass.</summary>
public class FlightSyncPlannerTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static readonly FlightSyncPlanOptions Options = new(
        FastInterval: TimeSpan.FromMinutes(3),
        LiveInterval: TimeSpan.FromMinutes(7),
        ScheduleCheckInterval: TimeSpan.FromHours(6),
        FastWindowBeforeDeparture: TimeSpan.FromHours(3),
        LiveWindowBeforeDeparture: TimeSpan.FromDays(1),
        StopAfterArrival: TimeSpan.FromMinutes(20),
        StopWithoutArrivalAfter: TimeSpan.FromHours(12));

    private static Flight Departing(DateTime departureUtc, DateTime? arrivalUtc = null) =>
        Flight.Create(1, "TK1028", 10, 20, DateOnly.FromDateTime(departureUtc),
            departureUtc, arrivalUtc ?? departureUtc.AddHours(3));

    private static FlightSyncPlan Plan(Flight flight, bool isTracked = true) =>
        FlightSyncPlanner.Plan(flight, isTracked, Now, Options);

    // ── none ──
    [Fact]
    public void Untracked_HasNoModeAndNoNextSync()
    {
        Assert.Equal(new FlightSyncPlan(null, null), Plan(Departing(Now.AddHours(1)), isTracked: false));
    }

    // ── Stopped ──
    [Fact]
    public void ArrivedExactlyStopAfterArrivalAgo_IsStopped()
    {
        var flight = Departing(Now.AddHours(-4));
        flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-4), Now - Options.StopAfterArrival, null, Now);
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Stopped, null), Plan(flight));
    }

    [Fact]
    public void ArrivedOneMinuteInsideStopAfterArrival_IsStillFast()
    {
        var flight = Departing(Now.AddHours(-4));
        flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-4), Now - Options.StopAfterArrival + OneMinute, null, Now);
        Assert.Equal(FlightSyncMode.Fast, Plan(flight).Mode);
    }

    [Fact]
    public void CancelledExactlyStopAfterArrivalAgo_IsStopped()
    {
        var flight = Departing(Now.AddHours(1));
        flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now - Options.StopAfterArrival);
        Assert.Equal(FlightSyncMode.Stopped, Plan(flight).Mode);
    }

    [Fact]
    public void CancelledOneMinuteInsideStopAfterArrival_IsNotStopped()
    {
        var flight = Departing(Now.AddHours(1));
        flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now - Options.StopAfterArrival + OneMinute);
        Assert.Equal(FlightSyncMode.Fast, Plan(flight).Mode);
    }

    [Fact]
    public void ArrivedRecently_ButCancelledLongerThanStopAfterArrivalAgo_IsStopped()
    {
        // The rule is an OR: a recent arrival doesn't keep an old cancellation syncing.
        var flight = Departing(Now.AddHours(-4));
        flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now - Options.StopAfterArrival - OneMinute);
        flight.ApplyStatusUpdate("CAN", null, null, Now.AddHours(-4), Now.AddMinutes(-5), null, Now.AddMinutes(-5));
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Stopped, null), Plan(flight));
    }

    [Fact]
    public void NoArrivalReported_ScheduledArrivalExactlyStopWithoutArrivalAgo_IsStopped()
    {
        var arrival = Now - Options.StopWithoutArrivalAfter;
        Assert.Equal(FlightSyncMode.Stopped, Plan(Departing(arrival.AddHours(-3), arrival)).Mode);
    }

    [Fact]
    public void NoArrivalReported_ScheduledArrivalOneMinuteInside_IsNotStopped()
    {
        var arrival = Now - Options.StopWithoutArrivalAfter + OneMinute;
        Assert.Equal(FlightSyncMode.Fast, Plan(Departing(arrival.AddHours(-3), arrival)).Mode);
    }

    [Fact]
    public void NoArrivalReported_EstimatedArrivalWinsOverScheduled()
    {
        // Scheduled arrival is long past, but the estimate says it hasn't landed yet: keep syncing.
        var scheduledArrival = Now - Options.StopWithoutArrivalAfter - TimeSpan.FromHours(1);
        var flight = Departing(scheduledArrival.AddHours(-3), scheduledArrival);
        flight.ApplyStatusUpdate("DLY", null, Now - Options.StopWithoutArrivalAfter + OneMinute, null, null, null, Now);
        Assert.Equal(FlightSyncMode.Fast, Plan(flight).Mode);
    }

    [Fact]
    public void RecentActualArrival_WinsOverAScheduledArrivalLongAgo()
    {
        var scheduledArrival = Now - Options.StopWithoutArrivalAfter - TimeSpan.FromHours(1);
        var flight = Departing(scheduledArrival.AddHours(-3), scheduledArrival);
        flight.ApplyStatusUpdate("ARR", null, null, null, Now.AddMinutes(-5), null, Now);
        Assert.Equal(FlightSyncMode.Fast, Plan(flight).Mode);
    }

    // ── Fast ──
    [Fact]
    public void DepartingExactlyFastWindowAway_IsFast_NextSyncAfterFastInterval()
    {
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Fast, Now + Options.FastInterval),
            Plan(Departing(Now + Options.FastWindowBeforeDeparture)));
    }

    [Fact]
    public void DepartingOneMinuteOutsideFastWindow_IsLive()
    {
        Assert.Equal(FlightSyncMode.Live, Plan(Departing(Now + Options.FastWindowBeforeDeparture + OneMinute)).Mode);
    }

    [Fact]
    public void EstimatedDeparture_MovesTheFastWindow()
    {
        // Delayed: scheduled inside the window, estimated outside it.
        var delayed = Departing(Now.AddHours(1));
        delayed.ApplyStatusUpdate("DLY", Now + Options.FastWindowBeforeDeparture + OneMinute, null, null, null, null, Now);
        Assert.Equal(FlightSyncMode.Live, Plan(delayed).Mode);

        // Still waiting to leave long after the scheduled time: stays Fast.
        var late = Departing(Now.AddHours(-2));
        late.ApplyStatusUpdate("DLY", Now.AddHours(1), null, null, null, null, Now);
        Assert.Equal(FlightSyncMode.Fast, Plan(late).Mode);
    }

    // ── Live ──
    [Fact]
    public void ScheduledExactlyLiveWindowAway_IsLive()
    {
        Assert.Equal(FlightSyncMode.Live, Plan(Departing(Now + Options.LiveWindowBeforeDeparture)).Mode);
    }

    [Fact]
    public void ScheduledOneMinuteOutsideLiveWindow_IsSchedule()
    {
        Assert.Equal(FlightSyncMode.Schedule, Plan(Departing(Now + Options.LiveWindowBeforeDeparture + OneMinute)).Mode);
    }

    [Fact]
    public void LiveWindow_UsesScheduledDeparture_NotTheEstimate()
    {
        var flight = Departing(Now + Options.LiveWindowBeforeDeparture + OneMinute);
        flight.ApplyStatusUpdate("SCH", Now + Options.LiveWindowBeforeDeparture - OneMinute, null, null, null, null, Now);
        Assert.Equal(FlightSyncMode.Schedule, Plan(flight).Mode);
    }

    [Fact]
    public void Live_FarFromFast_NextSyncAfterLiveInterval()
    {
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Live, Now + Options.LiveInterval),
            Plan(Departing(Now.AddHours(10))));
    }

    [Fact]
    public void Live_NextSyncCappedAtTheStartOfFast()
    {
        var departure = Now + Options.FastWindowBeforeDeparture + TimeSpan.FromMinutes(2); // Fast starts in 2 min < LiveInterval
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Live, departure - Options.FastWindowBeforeDeparture),
            Plan(Departing(departure)));
    }

    // ── Schedule ──
    [Fact]
    public void Schedule_FarFromLive_NextSyncAfterScheduleCheckInterval()
    {
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Schedule, Now + Options.ScheduleCheckInterval),
            Plan(Departing(Now.AddDays(30))));
    }

    [Fact]
    public void Schedule_NextSyncCappedAtTheStartOfLive()
    {
        var departure = Now + Options.LiveWindowBeforeDeparture + TimeSpan.FromHours(1); // Live starts in 1 h < 6 h
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Schedule, departure - Options.LiveWindowBeforeDeparture),
            Plan(Departing(departure)));
    }

    [Fact]
    public void LiveStartsAtUtc_IsScheduledDepartureMinusTheLiveWindow()
    {
        var departure = Now.AddDays(5);
        Assert.Equal(departure - Options.LiveWindowBeforeDeparture, FlightSyncPlanner.LiveStartsAtUtc(Departing(departure), Options));
    }

    // ── Options mapping ──
    private sealed class SyncOptions : IFlightSyncOptions
    {
        public TimeSpan NearDepartureInterval => TimeSpan.FromMinutes(1);
        public TimeSpan Interval => TimeSpan.FromMinutes(2);
        public TimeSpan ScheduleCheckInterval => TimeSpan.FromMinutes(3);
        public TimeSpan FastWindowBeforeDeparture => TimeSpan.FromMinutes(4);
        public TimeSpan StopAfterArrival => TimeSpan.FromMinutes(5);
        public TimeSpan StopWithoutArrivalAfter => TimeSpan.FromMinutes(6);
        public TimeSpan ManualSyncCooldown => TimeSpan.FromMinutes(4);
        public int MaxActiveTrackedFlightsPerTraveler => 3;
    }

    private sealed class WindowOptions : IFlightStatusWindowOptions
    {
        public int StatusWindowFutureDays => 1;
    }

    [Fact]
    public void From_MapsEveryOption()
    {
        Assert.Equal(new FlightSyncPlanOptions(
                FastInterval: TimeSpan.FromMinutes(1),
                LiveInterval: TimeSpan.FromMinutes(2),
                ScheduleCheckInterval: TimeSpan.FromMinutes(3),
                FastWindowBeforeDeparture: TimeSpan.FromMinutes(4),
                LiveWindowBeforeDeparture: TimeSpan.FromDays(1),
                StopAfterArrival: TimeSpan.FromMinutes(5),
                StopWithoutArrivalAfter: TimeSpan.FromMinutes(6)),
            FlightSyncPlanOptions.From(new SyncOptions(), new WindowOptions()));
    }
}
