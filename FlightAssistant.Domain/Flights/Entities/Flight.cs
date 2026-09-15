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

        // --- Status sync schedule (#356). Written only by the sync, tracking and untracking.
        public DateTime? LastSyncedUtc { get; private set; }
        public DateTime? NextSyncDueUtc { get; private set; }
        public FlightSyncMode? SyncMode { get; private set; }
        public DateTime? ScheduleMissingSinceUtc { get; private set; }

        /// <summary>When a status update first reported <c>CAN</c>; cleared by any later other code.
        /// <c>Flight</c> doesn't store its current status, and the Stopped rule needs this.</summary>
        public DateTime? CancelledAtUtc { get; private set; }

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

        public void ApplyStatusUpdate(
            string statusCode,
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc)
        {
            ApplyProviderTimes(estimatedDepartureUtc, estimatedArrivalUtc, actualDepartureUtc, actualArrivalUtc, equipmentAircraftIcao, nowUtc);
            CancelledAtUtc = statusCode == CancelledStatusCode ? CancelledAtUtc ?? nowUtc : null;
            UpdatedUtc = nowUtc;

            Raise(new FlightStatusChanged(Id, FlightNumber, ServiceDateUtc, statusCode, nowUtc));
        }

        /// <summary>
        /// Takes the provider's times for a status the flight already has (#351). Estimates and actuals are
        /// taken as reported, so a missing one clears the stored value, as <see cref="ApplyStatusUpdate"/>
        /// does. The aircraft type is taken only when reported. Raises no event, so it sends no push, and
        /// leaves <see cref="CancelledAtUtc"/> alone, since that follows the status.
        /// <see cref="UpdatedUtc"/> moves only when something changed.
        /// </summary>
        public void ApplyProviderTimes(
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc)
        {
            var changed = EstimatedDepartureUtc != estimatedDepartureUtc
                          || EstimatedArrivalUtc != estimatedArrivalUtc
                          || ActualDepartureUtc != actualDepartureUtc
                          || ActualArrivalUtc != actualArrivalUtc
                          || (equipmentAircraftIcao is not null && EquipmentAircraftIcao != equipmentAircraftIcao);

            EstimatedDepartureUtc = estimatedDepartureUtc;
            EstimatedArrivalUtc = estimatedArrivalUtc;
            ActualDepartureUtc = actualDepartureUtc;
            ActualArrivalUtc = actualArrivalUtc;
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
