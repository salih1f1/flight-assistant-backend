using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Alliance : SeedableEntity
    {
        public int Id { get; private set; }
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;

        public ICollection<Airline> Airlines { get; private set; } = new List<Airline>();

        private Alliance() { } // EF

        public static Alliance Create(string code, string name) => new() { Code = code, Name = name };
        public void Update(string name) { Name = name; }
    }
}
