namespace FlightAssistant.Contracts.Requests.Admin.Loyalty
{
    /// <summary>The new values for one of a loyalty program's tiers (#488). Every field is replaced.</summary>
    public record UpdateLoyaltyTierRequest(
        string Code,
        string Name,
        int Rank,
        string? AllianceCode,
        string? AllianceTierCode
    );
}
