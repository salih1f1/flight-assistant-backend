using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Alliance : SeadableEntity
    {
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;

        public ICollection<Airline> Airlines { get; private set; } = new List<Airline>();
    }
}
