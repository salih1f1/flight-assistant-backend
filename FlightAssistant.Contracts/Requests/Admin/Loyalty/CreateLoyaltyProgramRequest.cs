namespace FlightAssistant.Contracts.Requests.Admin.Loyalty
{
    public record CreateLoyaltyProgramRequest(
        string Code,
        string Name,
        int KindId,
        int? AirlineId,
        string? AllianceCode
    );
}
