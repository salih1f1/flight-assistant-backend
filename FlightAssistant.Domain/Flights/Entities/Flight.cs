using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Flights.Enums;
using FlightAssistant.Domain.Flights.Events;
using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Domain.Flights.Entities
{
    public sealed class Flight : BaseIntEntity, IAggregateRoot
    {
        public int AirlineId { get; private set; }
        public string FlightNumber { get; private set; } = default!;
        public int DepartureAirportId { get; private set; }
        public int ArrivalAirportId { get; private set; }

        // --- Times (always store UTC; convert to local for UI)
        public DateOnly ServiceDateUtc { get; private set; }
        public DateTime ScheduledDepartureUtc { get; private set; }
        public DateTime ScheduledArrivalUtc { get; private set; }
        public DateTime? EstimatedDepartureUtc { get; private set; }
        public DateTime? EstimatedArrivalUtc { get; private set; }
        public DateTime? ActualDepartureUtc { get; private set; }
        public DateTime? ActualArrivalUtc { get; private set; }

        public string? EquipmentAircraftIcao { get; private set; }
        public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; private set; } = DateTime.UtcNow;

        public const string CancelledStatusCode = "CAN";
        public const string DelayedStatusCode = "DLY";

        /// <summary>
        /// How late an estimated departure must be before a flight counts as delayed, and how much later a
        /// delay must grow before a traveler hears about it again (#352).
        /// </summary>
        public static readonly TimeSpan DelayThreshold = TimeSpan.FromMinutes(15);

        // --- Status sync schedule (#356). Written only by the sync, tracking and untracking.
        public DateTime? LastSyncedUtc { get; private set; }
        public DateTime? NextSyncDueUtc { get; private set; }
        public FlightSyncMode? SyncMode { get; private set; }
        public DateTime? ScheduleMissingSinceUtc { get; private set; }

        /// <summary>When a status update first reported <c>CAN</c>; cleared by any later other code.
        /// <c>Flight</c> doesn't store its current status, and the Stopped rule needs this.</summary>
        public DateTime? CancelledAtUtc { get; private set; }

        /// <summary>The estimated departure the last delay push went out for (#352). A delayed flight is pushed
        /// again only when its estimate moves at least <see cref="DelayThreshold"/> later than this, so small
        /// slips add up. Cleared by a departure, landing, cancellation or diversion; a silent SCH or UNK in
        /// between keeps it. Set even when nobody received the push: it belongs to the flight, not a traveler.</summary>
        public DateTime? DelayNotifiedDepartureUtc { get; private set; }

        public Airline Airline { get; private set; } = default!;

        private Flight() { } // EF

        public static Flight Create(
            int airlineId,
            string flightNumber,
            int departureAirportId,
            int arrivalAirportId,
            DateOnly serviceDate,
            DateTime scheduledDepartureUtc,
            DateTime scheduledArrivalUtc,
            string? equipmentAircraftIcao = null)
        {
            if (string.IsNullOrWhiteSpace(flightNumber))
                throw new ArgumentException("Flight number is required.", nameof(flightNumber));

            return new Flight
            {
                AirlineId = airlineId,
                FlightNumber = flightNumber.Trim().ToUpperInvariant(),
                DepartureAirportId = departureAirportId,
                ArrivalAirportId = arrivalAirportId,
                ServiceDateUtc = serviceDate,
                ScheduledDepartureUtc = scheduledDepartureUtc,
                ScheduledArrivalUtc = scheduledArrivalUtc,
                EquipmentAircraftIcao = equipmentAircraftIcao,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Records a new status from the provider and raises <see cref="FlightStatusChanged"/>, which the
        /// push handler acts on. The event says whether a push should go out (#352). For a delay with an
        /// estimate: when there was no delay push yet, or the estimate is at least <see cref="DelayThreshold"/>
        /// later than the last one's, whether or not the status changed. For any other status: when it
        /// changed (<paramref name="statusChanged"/>).
        /// A correction to an existing status (a revised actual departure, a small slip) records the event
        /// without a push. For a delay, the event carries how many minutes the estimate is after the
        /// provider's reported schedule.
        /// </summary>
        public void ApplyStatusUpdate(
            string statusCode,
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc,
            bool statusChanged = true,
            DateTime? reportedScheduledDepartureUtc = null,
            DateTime? reportedScheduledArrivalUtc = null)
        {
            ApplyProviderTimes(estimatedDepartureUtc, estimatedArrivalUtc, actualDepartureUtc, actualArrivalUtc, equipmentAircraftIcao, nowUtc,
                reportedScheduledDepartureUtc, reportedScheduledArrivalUtc);
            CancelledAtUtc = statusCode == CancelledStatusCode ? CancelledAtUtc ?? nowUtc : null;
            UpdatedUtc = nowUtc;

            var shouldNotify = statusChanged;
            int? delayMinutes = null;
            if (statusCode == DelayedStatusCode && estimatedDepartureUtc is { } estimate)
            {
                // From the provider's current schedule, the one the delay was detected against; a retime
                // leaves the stored ScheduledDepartureUtc stale.
                var scheduled = reportedScheduledDepartureUtc ?? ScheduledDepartureUtc;
                if (estimate > scheduled)
                    delayMinutes = (int)Math.Round((estimate - scheduled).TotalMinutes);

                // Only the remembered estimate decides, not whether the status changed: SCH and UNK send no
                // push, so a delay bouncing around the threshold through one of them would otherwise push on
                // every return to DLY (#352).
                shouldNotify = DelayNotifiedDepartureUtc is null
                               || estimate >= DelayNotifiedDepartureUtc.Value + DelayThreshold;

                if (shouldNotify)
                    DelayNotifiedDepartureUtc = estimate;
            }
            else if (IsAnnouncedToTravelers(statusCode))
            {
                // A departure, landing, cancellation or diversion was news to travelers, so a later delay (a
                // reinstated flight) starts fresh.
                DelayNotifiedDepartureUtc = null;
            }

            Raise(new FlightStatusChanged(Id, FlightNumber, ServiceDateUtc, statusCode, nowUtc, shouldNotify, delayMinutes));
        }

        /// <summary>The statuses, besides a delay, that travelers are pushed about. <c>SCH</c> and <c>UNK</c>
        /// send no push, so a delay's remembered estimate survives them. Keep in step with the push handler's
        /// messages.</summary>
        private static bool IsAnnouncedToTravelers(string statusCode) =>
            statusCode is "DEP" or "ARR" or CancelledStatusCode or "DIV";

        /// <summary>
        /// Takes the provider's times (#351). Estimates are taken as reported, so a missing one clears the
        /// stored value. An actual departure or arrival, once reported, is kept when a later response omits
        /// it: clearing an actual arrival would send a landed flight back to the planner's no-arrival rule
        /// and up to a day of Fast polls the budget guard doesn't hold back. The aircraft type is taken only
        /// when reported. The scheduled departure and arrival (#399) follow the same keep-when-omitted rule as
        /// the actual times — these columns are non-nullable, and a provider that omits one has not reported
        /// the flight as unscheduled — so a retime updates them, and a response with no schedule leaves the
        /// stored one alone; the sync planner's Live/Fast windows and the tracking cap's stop rule both read
        /// these fields directly, so a stale value keeps working from when the flight was first tracked.
        /// Raises no event, so it sends no push, and leaves <see cref="CancelledAtUtc"/> alone,
        /// since that follows the status. <see cref="UpdatedUtc"/> moves only when something changed.
        /// <see cref="ApplyStatusUpdate"/> uses it too, so both paths keep actual times the same way.
        /// </summary>
        public void ApplyProviderTimes(
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc,
            DateTime? reportedScheduledDepartureUtc = null,
            DateTime? reportedScheduledArrivalUtc = null)
        {
            var actualDeparture = actualDepartureUtc ?? ActualDepartureUtc;
            var actualArrival = actualArrivalUtc ?? ActualArrivalUtc;

            // An airline retimes a flight, sometimes by hours (#399). The stored schedule is written once at
            // tracking and would otherwise stay wrong for the flight's whole life — the planner's Live window
            // reads it directly, and the tracking cap's stop rule reads the arrival. A provider that omits a
            // scheduled time keeps the stored one: these are non-nullable, and "not reported" is not "unknown".
            var scheduledDeparture = reportedScheduledDepartureUtc ?? ScheduledDepartureUtc;
            var scheduledArrival = reportedScheduledArrivalUtc ?? ScheduledArrivalUtc;

            var changed = EstimatedDepartureUtc != estimatedDepartureUtc
                          || EstimatedArrivalUtc != estimatedArrivalUtc
                          || ActualDepartureUtc != actualDeparture
                          || ActualArrivalUtc != actualArrival
                          || ScheduledDepartureUtc != scheduledDeparture
                          || ScheduledArrivalUtc != scheduledArrival
                          || (equipmentAircraftIcao is not null && EquipmentAircraftIcao != equipmentAircraftIcao);

            EstimatedDepartureUtc = estimatedDepartureUtc;
            EstimatedArrivalUtc = estimatedArrivalUtc;
            ActualDepartureUtc = actualDeparture;
            ActualArrivalUtc = actualArrival;
            ScheduledDepartureUtc = scheduledDeparture;
            ScheduledArrivalUtc = scheduledArrival;
            if (equipmentAircraftIcao is not null)
                EquipmentAircraftIcao = equipmentAircraftIcao;

            if (changed)
                UpdatedUtc = nowUtc;
        }

        /// <summary>Records a check against the provider, whether or not anything changed.</summary>
        public void MarkSynced(DateTime syncedUtc) => LastSyncedUtc = syncedUtc;

        /// <summary>
        /// Raises <see cref="FlightTracked"/>: called on every track request, whether the tracking
        /// link is new or the traveler already tracked this flight (#410, R1), since a repeat track
        /// can still find the airline or an airport newly unverified or stale. Stores no state of its
        /// own — the handler that reacts to the event owns every verification decision (R2).
        /// </summary>
        public void MarkTracked(DateTime nowUtc) =>
            Raise(new FlightTracked(Id, AirlineId, DepartureAirportId, ArrivalAirportId, nowUtc));

        /// <summary>
        /// The daily schedule check found the flight (#444, #349 §2.4): stores the airline's current
        /// scheduled times and clears <see cref="ScheduleMissingSinceUtc"/>. Returns whether the times
        /// actually moved, which is what separates the caller's ScheduleUpdated outcome from NoChange.
        ///
        /// <para>
        /// The flag is cleared on any sighting, retimed or not: it means "the airline stopped listing
        /// this flight", and that stops being true the moment it is listed again. <see cref="UpdatedUtc"/>
        /// moves only when a time changed, matching <see cref="ApplyProviderTimes"/>. Raises no event —
        /// a schedule check is not a status, so it must never push.
        /// </para>
        /// </summary>
        public bool ApplyScheduleUpdate(DateTime scheduledDepartureUtc, DateTime scheduledArrivalUtc, DateTime nowUtc)
        {
            ScheduleMissingSinceUtc = null;

            var changed = ScheduledDepartureUtc != scheduledDepartureUtc || ScheduledArrivalUtc != scheduledArrivalUtc;
            if (!changed) return false;

            ScheduledDepartureUtc = scheduledDepartureUtc;
            ScheduledArrivalUtc = scheduledArrivalUtc;
            UpdatedUtc = nowUtc;
            return true;
        }

        /// <summary>
        /// The daily schedule check didn't find the flight (#444). Remembers when it <em>first</em> went
        /// missing and keeps that moment on every later check, because the useful fact is how long it has
        /// been gone, not that it is still gone. The flight stays tracked and keeps its daily check.
        ///
        /// <para>
        /// Leaves <see cref="UpdatedUtc"/> alone, like <see cref="ApplySyncPlan"/> below: this is the
        /// check's own bookkeeping, not a change to the flight the airline published.
        /// </para>
        /// </summary>
        public void MarkMissingFromSchedule(DateTime nowUtc) => ScheduleMissingSinceUtc ??= nowUtc;

        /// <summary>
        /// Stores the planner's mode and next due time. Leaves <see cref="UpdatedUtc"/> alone: this
        /// changes every few minutes and isn't a change to the flight.
        /// </summary>
        public void ApplySyncPlan(FlightSyncMode? mode, DateTime? nextSyncDueUtc)
        {
            if (nextSyncDueUtc is not null && mode is null or FlightSyncMode.Stopped)
                throw new ArgumentException("A flight that isn't synced can't have a next sync time.", nameof(nextSyncDueUtc));

            SyncMode = mode;
            NextSyncDueUtc = nextSyncDueUtc;
        }
    }
}
