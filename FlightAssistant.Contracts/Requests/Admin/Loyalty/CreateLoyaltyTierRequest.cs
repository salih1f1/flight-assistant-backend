namespace FlightAssistant.Contracts.Requests.Admin.Loyalty
{
    /// <summary>A tier to add to a loyalty program (#488). Rank orders the program's tiers, lowest first.</summary>
    public record CreateLoyaltyTierRequest(
        string Code,
        string Name,
        int Rank,
        string? AllianceCode,
        string? AllianceTierCode
    );
}
