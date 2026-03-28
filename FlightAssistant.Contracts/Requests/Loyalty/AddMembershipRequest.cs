namespace FlightAssistant.Contracts.Requests.Loyalty
{
    public record AddMembershipRequest(
        int ProgramId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate,
        DateOnly? PointsExpiryDate
    );
}
