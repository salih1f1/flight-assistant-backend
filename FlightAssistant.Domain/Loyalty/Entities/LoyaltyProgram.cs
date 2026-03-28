using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Loyalty.Entities
{
    public sealed class LoyaltyProgram : IAggregateRoot
    {
        public int Id { get; private set; }
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;

        public int KindId { get; private set; }                 // FK -> loyalty_program_kinds.Id
        public LoyaltyProgramKind Kind { get; private set; } = default!;

        public int? AirlineId { get; private set; }             // FK -> airlines (optional)
        public string? AllianceCode { get; private set; }       // "STAR","OW","ST" (optional)

        public ICollection<LoyaltyTier> Tiers { get; private set; } = new List<LoyaltyTier>();

        public static LoyaltyProgram Create(string code, string name, int kindId, int? airlineId = null, string? allianceCode = null) =>
            new()
            {
                Code = code,
                Name = name,
                KindId = kindId,
                AirlineId = airlineId,
                AllianceCode = allianceCode
            };

        public void Update(string code, string name, int kindId, int? airlineId, string? allianceCode)
        {
            Code = code;
            Name = name;
            KindId = kindId;
            AirlineId = airlineId;
            AllianceCode = allianceCode;
        }

        public void AddTier(LoyaltyTier tier) => Tiers.Add(tier);

        public void RemoveTier(int tierId)
        {
            var tier = Tiers.FirstOrDefault(t => t.Id == tierId);
            if (tier is not null) Tiers.Remove(tier);
        }
    }
}
