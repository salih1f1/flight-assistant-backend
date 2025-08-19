using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Flights.Entities
{
    public class TravelerFlight : IAggregateRoot
    {
        public Guid Id { get; private set; } = Guid.NewGuid();
        public Guid TravelerId { get; private set; }
        public int FlightId { get; private set; }
        public bool Notify { get; private set; } = true;
        public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;
    }
}
