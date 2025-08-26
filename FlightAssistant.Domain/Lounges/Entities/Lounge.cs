using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Lounges.Entities
{
    public class Lounge : IAggregateRoot
    {
        public int Id { get; private set; }
        public int AirportId { get; private set; }
        public string Name { get; private set; } = default!;
        public string? Terminal { get; private set; }
        public string? LocationHint { get; private set; }
        public string? Hours { get; private set; }
        public bool IsAirside { get; private set; } = true;
        public bool IsDomesticOnly { get; private set; }
        public bool IsInternationalOnly { get; private set; }
        public string? Amenities { get; private set; }

        public ICollection<LoungeProviderLink> Providers { get; private set; } = new List<LoungeProviderLink>();
    }
}
