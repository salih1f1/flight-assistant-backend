namespace FlightAssistant.Domain.Loyalty.Entities
{
    public sealed class LoyaltyTier
    {
        public int Id { get; private set; }
        public int ProgramId { get; private set; }
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;
        public int Rank { get; private set; }

        public string? AllianceCode { get; private set; }
        public string? AllianceTierCode { get; private set; }
    }
}
