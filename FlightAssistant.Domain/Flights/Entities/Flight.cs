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

        /// <summary>The estimated departure the last delay push went out for (#352). A flight that stays
        /// delayed is pushed again only when its estimate moves at least <see cref="DelayThreshold"/> later
        /// than this, so small slips add up. Never cleared: a return to delayed is a status change and pushes
        /// anyway.</summary>
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
        /// push handler acts on. The event says whether a push should go out (#352): when the status
        /// changed (<paramref name="statusChanged"/>), or, for a flight that stays delayed, once its
        /// estimated departure is at least <see cref="DelayThreshold"/> later than the last delay push's.
        /// A correction to an existing status (a revised actual departure, a small slip) records the event
        /// without a push. For a delay, the event carries how many minutes late the estimate is.
        /// </summary>
        public void ApplyStatusUpdate(
            string statusCode,
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc,
            bool statusChanged = true)
        {
            ApplyProviderTimes(estimatedDepartureUtc, estimatedArrivalUtc, actualDepartureUtc, actualArrivalUtc, equipmentAircraftIcao, nowUtc);
            CancelledAtUtc = statusCode == CancelledStatusCode ? CancelledAtUtc ?? nowUtc : null;
            UpdatedUtc = nowUtc;

            var shouldNotify = statusChanged;
            int? delayMinutes = null;
            if (statusCode == DelayedStatusCode && estimatedDepartureUtc is { } estimate)
            {
                if (estimate > ScheduledDepartureUtc)
                    delayMinutes = (int)Math.Round((estimate - ScheduledDepartureUtc).TotalMinutes);

                shouldNotify = shouldNotify
                               || DelayNotifiedDepartureUtc is null
                               || estimate >= DelayNotifiedDepartureUtc.Value + DelayThreshold;

                if (shouldNotify)
                    DelayNotifiedDepartureUtc = estimate;
            }

            Raise(new FlightStatusChanged(Id, FlightNumber, ServiceDateUtc, statusCode, nowUtc, shouldNotify, delayMinutes));
        }

        /// <summary>
        /// Takes the provider's times (#351). Estimates are taken as reported, so a missing one clears the
        /// stored value. An actual departure or arrival, once reported, is kept when a later response omits
        /// it: clearing an actual arrival would send a landed flight back to the planner's no-arrival rule
        /// and up to a day of Fast polls the budget guard doesn't hold back. The aircraft type is taken only
        /// when reported. Raises no event, so it sends no push, and leaves <see cref="CancelledAtUtc"/> alone,
        /// since that follows the status. <see cref="UpdatedUtc"/> moves only when something changed.
        /// <see cref="ApplyStatusUpdate"/> uses it too, so both paths keep actual times the same way.
        /// </summary>
        public void ApplyProviderTimes(
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc)
        {
            var actualDeparture = actualDepartureUtc ?? ActualDepartureUtc;
            var actualArrival = actualArrivalUtc ?? ActualArrivalUtc;

            var changed = EstimatedDepartureUtc != estimatedDepartureUtc
                          || EstimatedArrivalUtc != estimatedArrivalUtc
                          || ActualDepartureUtc != actualDeparture
                          || ActualArrivalUtc != actualArrival
                          || (equipmentAircraftIcao is not null && EquipmentAircraftIcao != equipmentAircraftIcao);

            EstimatedDepartureUtc = estimatedDepartureUtc;
            EstimatedArrivalUtc = estimatedArrivalUtc;
            ActualDepartureUtc = actualDeparture;
            ActualArrivalUtc = actualArrival;
            if (equipmentAircraftIcao is not null)
                EquipmentAircraftIcao = equipmentAircraftIcao;

            if (changed)
                UpdatedUtc = nowUtc;
        }

        /// <summary>Records a check against the provider, whether or not anything changed.</summary>
        public void MarkSynced(DateTime syncedUtc) => LastSyncedUtc = syncedUtc;

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
