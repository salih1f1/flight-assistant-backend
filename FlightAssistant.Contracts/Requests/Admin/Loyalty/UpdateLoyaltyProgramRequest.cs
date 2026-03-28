namespace FlightAssistant.Contracts.Requests.Admin.Loyalty
{
    public record UpdateLoyaltyProgramRequest(
        string Code,
        string Name,
        int KindId,
        int? AirlineId,
        string? AllianceCode
    );
}
