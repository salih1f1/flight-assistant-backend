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
        public DateOnly? PointsExpiryDate { get; private set; }
        public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; private set; } = DateTime.UtcNow;
    }
}
