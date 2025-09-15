using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Country : SeadableEntity
    {
        public string Code { get; init; } = default!;
        public string Name { get; init; } = default!;
        public string? Alpha3 { get; init; }
        public int? NumericCode { get; init; }
        public string? Region { get; init; }
        public string? Subregion { get; init; }

        public bool? IsEU { get; init; }
        public bool? IsSchengen { get; init; }
        public bool? IsEEA { get; init; }
    }
}
