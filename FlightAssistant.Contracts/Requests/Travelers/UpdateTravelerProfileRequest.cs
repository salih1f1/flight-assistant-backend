namespace FlightAssistant.Contracts.Requests.Travelers
{
    public record UpdateTravelerProfileRequest(
        string? NationalityCountryCode,
        string? ResidenceCountryCode
    );
}
