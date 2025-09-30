namespace FlightAssistant.Domain.Configuration.Entities
{
    public class SeedVersion
    {
        public string Dataset { get; set; } = default!;
        public int Version { get; set; }
        public DateTime AppliedAt { get; set; }
    }
}
