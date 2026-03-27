namespace FlightAssistant.Contracts.Responses.Travelers
{
    public record TravelerDto(
        Guid Id,
        string Email,
        string? DisplayName,
        string? CountryCode,
        string? NationalityCountryCode,
        string? ResidenceCountryCode
    );
}
