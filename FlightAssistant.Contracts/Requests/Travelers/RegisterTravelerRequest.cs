namespace FlightAssistant.Contracts.Requests.Travelers
{
    public record RegisterTravelerRequest(
        string Email,
        string? DisplayName
    );
}
