namespace FlightAssistant.Domain.Loyalty.Entities
{
    public sealed class TravelerLoyaltyMembership
    {
        public Guid Id { get; private set; } = Guid.NewGuid();
        public Guid TravelerId { get; private set; }
        public int ProgramId { get; private set; }
        public int? TierId { get; private set; }
        public string AccountNumber { get; private set; } = default!;
        public DateOnly? TierExpiryDate { get; private set; }
        public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; private set; } = DateTime.UtcNow;

        private TravelerLoyaltyMembership() { } // EF

        public static TravelerLoyaltyMembership Create(
            Guid travelerId,
            int programId,
            string accountNumber,
            int? tierId = null,
            DateOnly? tierExpiryDate = null) => new()
        {
            TravelerId = travelerId,
            ProgramId = programId,
            AccountNumber = accountNumber,
            TierId = tierId,
            TierExpiryDate = tierExpiryDate
        };

        public void Update(string accountNumber, int? tierId, DateOnly? tierExpiryDate)
        {
            AccountNumber = accountNumber;
            TierId = tierId;
            TierExpiryDate = tierExpiryDate;
            UpdatedUtc = DateTime.UtcNow;
        }
    }
}
