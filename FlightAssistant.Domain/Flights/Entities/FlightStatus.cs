namespace FlightAssistant.Domain.Flights.Entities
{
    public class FlightStatus
    {
        public int Id { get; private set; }
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;
        public string? Description { get; private set; }
    }
}
