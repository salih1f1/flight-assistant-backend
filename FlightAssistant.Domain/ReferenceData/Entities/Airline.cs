namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Airline
    {
        public int Id { get; private set; }
        public string Iata { get; private set; } = default!;
        public string? Icao { get; private set; }
        public string Name { get; private set; } = default!;
        public string CountryCode { get; private set; } = default!;

        public string? AllianceCode { get; private set; }
        public Alliance? Alliance { get; private set; }
    }
}
