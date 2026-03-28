namespace FlightAssistant.Contracts.Responses.Loyalty
{
    public record LoyaltyMembershipDto(
        Guid Id,
        int ProgramId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate,
        DateOnly? PointsExpiryDate
    );
}
