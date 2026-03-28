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

        public static LoyaltyTier Create(int programId, string code, string name, int rank,
            string? allianceCode = null, string? allianceTierCode = null) =>
            new()
            {
                ProgramId = programId,
                Code = code,
                Name = name,
                Rank = rank,
                AllianceCode = allianceCode,
                AllianceTierCode = allianceTierCode
            };

        public void Update(string code, string name, int rank, string? allianceCode, string? allianceTierCode)
        {
            Code = code;
            Name = name;
            Rank = rank;
            AllianceCode = allianceCode;
            AllianceTierCode = allianceTierCode;
        }
    }
}
