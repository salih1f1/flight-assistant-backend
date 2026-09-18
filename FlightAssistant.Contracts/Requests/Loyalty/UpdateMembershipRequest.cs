namespace FlightAssistant.Contracts.Requests.Loyalty
{
    public record UpdateMembershipRequest(
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate
    );
}
