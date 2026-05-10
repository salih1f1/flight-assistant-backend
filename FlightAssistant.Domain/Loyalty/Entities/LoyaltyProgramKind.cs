using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Loyalty.Entities
{
    public sealed class LoyaltyProgramKind : IVerifiable
    {
        public int Id { get; private set; }
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;
        public string? Description { get; private set; }
        public bool IsVerified { get; private set; }
        public DateTime? VerifiedAt { get; private set; }
        public string? VerifiedBy { get; private set; }

        public ICollection<LoyaltyProgram> Programs { get; private set; } = new List<LoyaltyProgram>();

        private LoyaltyProgramKind() { }

        public static LoyaltyProgramKind Create(string code, string name, string? description = null) =>
            new() { Code = code, Name = name, Description = description };

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
    }
}
