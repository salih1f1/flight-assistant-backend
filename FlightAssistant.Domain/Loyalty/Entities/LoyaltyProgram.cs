using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Loyalty.Entities
{
    public sealed class LoyaltyProgram : IAggregateRoot, IVerifiable
    {
        public int Id { get; private set; }
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;

        public int KindId { get; private set; }                 // FK -> loyalty_program_kinds.Id
        public LoyaltyProgramKind Kind { get; private set; } = default!;

        public int? AirlineId { get; private set; }             // FK -> airlines (optional)
        public string? AllianceCode { get; private set; }       // "STAR","OW","ST" (optional)

        public bool IsVerified { get; private set; }
        public DateTime? VerifiedAt { get; private set; }
        public string? VerifiedBy { get; private set; }

        private readonly List<LoyaltyTier> _tiers = new();
        public IReadOnlyCollection<LoyaltyTier> Tiers => _tiers;

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
            if (string.IsNullOrWhiteSpace(code))
                throw new DomainException("Loyalty program code is required.");
            if (string.IsNullOrWhiteSpace(name))
                throw new DomainException("Loyalty program name is required.");

            Code = code;
            Name = name;
            KindId = kindId;
            AirlineId = airlineId;
            AllianceCode = allianceCode;
        }

        public void Verify(string verifiedBy)
        {
            IsVerified = true;
            VerifiedAt = DateTime.UtcNow;
            VerifiedBy = verifiedBy;
        }

        public void Unverify()
        {
            IsVerified = false;
            VerifiedAt = null;
            VerifiedBy = null;
        }

        public void AddTier(LoyaltyTier tier) => _tiers.Add(tier);

        public void RemoveTier(int tierId)
        {
            var tier = _tiers.FirstOrDefault(t => t.Id == tierId);
            if (tier is not null) _tiers.Remove(tier);
        }

        public void ReplaceAllTiers(IEnumerable<LoyaltyTier> newTiers)
        {
            _tiers.Clear();
            foreach (var t in newTiers)
                _tiers.Add(t);
        }
    }
}
