using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Alliance : SeedableEntity
    {
        public string Code { get; init; } = default!;
        public string Name { get; init; } = default!;

        public ICollection<Airline> Airlines { get; private set; } = new List<Airline>();
    }
}
