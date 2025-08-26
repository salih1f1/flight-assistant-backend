using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Domain.Flights.Entities
{
    public class Flight
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

        public Airline Airline { get; private set; }
    }
}
