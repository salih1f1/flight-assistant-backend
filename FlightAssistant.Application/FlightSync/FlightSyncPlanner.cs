using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;

namespace FlightAssistant.Application.FlightSync
{
    public sealed record FlightSyncPlan(FlightSyncMode? Mode, DateTime? NextSyncDueUtc);

    public sealed record FlightSyncPlanOptions(
        TimeSpan FastInterval,
        TimeSpan LiveInterval,
        TimeSpan ScheduleCheckInterval,
        TimeSpan FastWindowBeforeDeparture,
        TimeSpan LiveWindowBeforeDeparture,
        TimeSpan StopAfterArrival,
        TimeSpan StopWithoutArrivalAfter)
    {
        public static FlightSyncPlanOptions From(IFlightSyncOptions sync, IFlightStatusWindowOptions window) => new(
            FastInterval: sync.NearDepartureInterval,
            LiveInterval: sync.Interval,
            ScheduleCheckInterval: sync.ScheduleCheckInterval,
            FastWindowBeforeDeparture: sync.FastWindowBeforeDeparture,
            LiveWindowBeforeDeparture: TimeSpan.FromDays(window.StatusWindowFutureDays),
            StopAfterArrival: sync.StopAfterArrival,
            StopWithoutArrivalAfter: sync.StopWithoutArrivalAfter);
    }

    /// <summary>
    /// Decides how often a flight is synced (spec §1). Rules, first match wins: untracked → none;
    /// Stopped; Fast; Live; Schedule. The next sync is capped at the moment the mode would change,
    /// so a stored mode is never more than one loop out of date (§1.1).
    /// </summary>
    public static class FlightSyncPlanner
    {
        public static FlightSyncPlan Plan(Flight flight, bool isTracked, DateTime nowUtc, FlightSyncPlanOptions options)
        {
            if (!isTracked)
                return new FlightSyncPlan(null, null);

            if (IsStopped(flight, nowUtc, options))
                return new FlightSyncPlan(FlightSyncMode.Stopped, null);

            var fastStartsAt = (flight.EstimatedDepartureUtc ?? flight.ScheduledDepartureUtc) - options.FastWindowBeforeDeparture;
            if (fastStartsAt <= nowUtc)
                return new FlightSyncPlan(FlightSyncMode.Fast, nowUtc + options.FastInterval);

            var liveStartsAt = LiveStartsAtUtc(flight, options);
            if (liveStartsAt <= nowUtc)
                return new FlightSyncPlan(FlightSyncMode.Live, Earliest(nowUtc + options.LiveInterval, fastStartsAt));

            return new FlightSyncPlan(FlightSyncMode.Schedule, Earliest(nowUtc + options.ScheduleCheckInterval, liveStartsAt));
        }

        /// <summary>When the status provider can first answer for the flight (#343). From the
        /// scheduled departure, not the service date, because the provider's bound is an exact time.</summary>
        public static DateTime LiveStartsAtUtc(Flight flight, FlightSyncPlanOptions options) =>
            flight.ScheduledDepartureUtc - options.LiveWindowBeforeDeparture;

        private static bool IsStopped(Flight flight, DateTime nowUtc, FlightSyncPlanOptions options)
        {
            // Keep syncing for a while after arrival or cancellation, so the final status, gate and
            // baggage claim are still recorded (#345).
            if (flight.ActualArrivalUtc is { } arrived)
                return arrived <= nowUtc - options.StopAfterArrival;

            if (flight.CancelledAtUtc is { } cancelled && cancelled <= nowUtc - options.StopAfterArrival)
                return true;

            // A flight that never reports an arrival.
            return (flight.EstimatedArrivalUtc ?? flight.ScheduledArrivalUtc) <= nowUtc - options.StopWithoutArrivalAfter;
        }

        private static DateTime Earliest(DateTime a, DateTime b) => a <= b ? a : b;
    }
}
