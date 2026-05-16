using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Country : SeedableEntity
    {
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;
        public string? Alpha3 { get; private set; }
        public int? NumericCode { get; private set; }
        public string? Region { get; private set; }
        public string? Subregion { get; private set; }

        public bool? IsEU { get; private set; }
        public bool? IsSchengen { get; private set; }
        public bool? IsEEA { get; private set; }

        public static Country Create(string code, string name, string? alpha3 = null, int? numericCode = null,
            string? region = null, string? subregion = null, bool? isEU = null, bool? isSchengen = null, bool? isEEA = null) =>
            new() { Code = code, Name = name, Alpha3 = alpha3, NumericCode = numericCode, Region = region, Subregion = subregion, IsEU = isEU, IsSchengen = isSchengen, IsEEA = isEEA };

        public void Update(string name, string? alpha3, int? numericCode, string? region, string? subregion, bool? isEU, bool? isSchengen, bool? isEEA)
        {
            Name = name;
            Alpha3 = alpha3;
            NumericCode = numericCode;
            Region = region;
            Subregion = subregion;
            IsEU = isEU;
            IsSchengen = isSchengen;
            IsEEA = isEEA;
        }
    }
}
