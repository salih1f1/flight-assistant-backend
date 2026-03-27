using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Domain.Flights.Entities
{
    public class Flight : IAggregateRoot
    {
        public int Id { get; private set; }

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
        public string? ExternalId { get; private set; }
        public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; private set; } = DateTime.UtcNow;

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
            string? externalId = null,
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
                ExternalId = externalId,
                EquipmentAircraftIcao = equipmentAircraftIcao,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        public void ApplyStatusUpdate(
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao)
        {
            EstimatedDepartureUtc = estimatedDepartureUtc;
            EstimatedArrivalUtc = estimatedArrivalUtc;
            ActualDepartureUtc = actualDepartureUtc;
            ActualArrivalUtc = actualArrivalUtc;
            if (equipmentAircraftIcao is not null)
                EquipmentAircraftIcao = equipmentAircraftIcao;
            UpdatedUtc = DateTime.UtcNow;
        }
    }
}
