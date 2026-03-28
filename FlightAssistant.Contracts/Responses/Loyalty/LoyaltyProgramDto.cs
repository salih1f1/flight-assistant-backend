namespace FlightAssistant.Contracts.Responses.Loyalty
{
    public record LoyaltyTierDto(int Id, string Code, string Name, int Rank, string? AllianceCode, string? AllianceTierCode);

    public record LoyaltyProgramDto(
        int Id,
        string Code,
        string Name,
        string Kind,
        int? AirlineId,
        string? AllianceCode,
        IReadOnlyList<LoyaltyTierDto> Tiers
    );
}
