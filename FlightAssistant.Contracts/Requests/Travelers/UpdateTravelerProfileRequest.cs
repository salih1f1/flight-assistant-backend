namespace FlightAssistant.Contracts.Requests.Travelers
{
    public record UpdateTravelerProfileRequest(
        string? DisplayName,
        string? CountryCode,
        string? NationalityCountryCode,
        string? ResidenceCountryCode
    );
}
