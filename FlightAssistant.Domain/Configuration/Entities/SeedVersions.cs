namespace FlightAssistant.Domain.Configuration.Entities
{
    public class SeedVersion
    {
        public string Dataset { get; init; } = default!;
        public int Version { get; init; }
        public DateTime AppliedAt { get; init; }
    }
}
