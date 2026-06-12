using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Common
{
    public class SeedableEntity : ISeedableEntity, IVerifiable
    {
        public string SourceType { get; set; } = "seed";
        public string? SourceSystem { get; set; }
        public string? ExternalIds { get; set; } // JSON string of key-value pairs
        public string Status { get; set; } = "active";
        public DateTime FirstSeenAt { get; protected set; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; protected set; } = DateTime.UtcNow;
        public string? Notes { get; set; }

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
