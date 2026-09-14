using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Common
{
    public class SeedableEntity : ISeedableEntity, IVerifiable
    {
        public string SourceType { get; set; } = "seed";
        public DateTime FirstSeenAt { get; protected set; } = DateTime.UtcNow;

        public bool IsVerified { get; private set; } = false;
        public DateTime? VerifiedAt { get; private set; }
        public string? VerifiedBy { get; private set; }
        public string? VerificationSource { get; private set; }
        public string? VerificationIssue { get; private set; }

        protected SeedableEntity() { } // EF

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
    }
}
