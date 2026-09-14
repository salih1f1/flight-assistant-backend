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

        /// <summary>
        /// When an admin or an import added the program. Null when unknown: rows from before this
        /// column existed, and rows from the seed files, which nobody added (#334). The dashboard
        /// counts only a known date as new.
        /// </summary>
        public DateTime? CreatedUtc { get; private set; }

        public bool IsVerified { get; private set; }
        public DateTime? VerifiedAt { get; private set; }
        public string? VerifiedBy { get; private set; }
        public string? VerificationSource { get; private set; }
        public string? VerificationIssue { get; private set; }

        private readonly List<LoyaltyTier> _tiers = new();
        public IReadOnlyCollection<LoyaltyTier> Tiers => _tiers;

        public static LoyaltyProgram Create(string code, string name, int kindId, int? airlineId = null, string? allianceCode = null) =>
            new()
            {
                Code = code,
                Name = name,
                KindId = kindId,
                AirlineId = airlineId,
                AllianceCode = allianceCode,
                CreatedUtc = DateTime.UtcNow
            };

        /// <summary>A program from the seed files: as <see cref="Create"/>, but with no creation time.</summary>
        public static LoyaltyProgram CreateSeeded(string code, string name, int kindId, int? airlineId = null, string? allianceCode = null)
        {
            var program = Create(code, name, kindId, airlineId, allianceCode);
            program.CreatedUtc = null;
            return program;
        }

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

        public void Verify(string verifiedBy, string source)
        {
            IsVerified = true;
            VerifiedAt = DateTime.UtcNow;
            VerifiedBy = verifiedBy;
            VerificationSource = source;
            VerificationIssue = null;
        }

        public void Unverify()
        {
            IsVerified = false;
            VerifiedAt = null;
            VerifiedBy = null;
            VerificationSource = null;
            VerificationIssue = null;
        }

        public void MarkVerificationFailed(string issue)
        {
            IsVerified = false;
            VerifiedAt = null;
            VerifiedBy = null;
            VerificationSource = null;
            VerificationIssue = issue;
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
