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

        protected SeedableEntity() { } // EF

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
