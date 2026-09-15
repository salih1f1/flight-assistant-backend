using FlightAssistant.Domain.Common;
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
        public string? VerificationSource { get; private set; }
        public string? VerificationIssue { get; private set; }

        public ICollection<LoyaltyProgram> Programs { get; private set; } = new List<LoyaltyProgram>();

        private LoyaltyProgramKind() { }

        public static LoyaltyProgramKind Create(string code, string name, string? description = null) =>
            new() { Code = code, Name = name, Description = description };

        public void Update(string name, string? description)
        {
            Name = name;
            Description = description;
        }

        public void Verify(string verifiedBy, string source) => Verify(verifiedBy, source, DateTime.UtcNow);

        public void Verify(string verifiedBy, string source, DateTime verifiedAtUtc)
        {
            VerificationGuard.EnsureValidVerify(verifiedBy, source, verifiedAtUtc);

            IsVerified = true;
            VerifiedAt = verifiedAtUtc;
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
            VerificationGuard.EnsureValidIssue(issue);

            IsVerified = false;
            VerifiedAt = null;
            VerifiedBy = null;
            VerificationSource = null;
            VerificationIssue = issue;
        }
    }
}
