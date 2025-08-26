namespace FlightAssistant.Domain.Flights.Entities
{
    public sealed class FlightStatusEvent
    {
        public long Id { get; private set; }
        public int FlightId { get; private set; }
        public int StatusId { get; private set; }

        public DateTime EffectiveAtUtc { get; private set; }
        public DateTime RecordedAtUtc { get; private set; }
        public string Source { get; private set; } = "manual";
        public string? ExternalId { get; private set; }
        public string? Gate { get; private set; }
        public string? Terminal { get; private set; }
        public string? BaggageClaim { get; private set; }
        public string? Remarks { get; private set; }
        public string? RawPayloadJson { get; private set; }
        public string DedupHash { get; private set; } = default!;

        public Flight Flight { get; private set; }
        public FlightStatus Status { get; private set; }
    }
}
