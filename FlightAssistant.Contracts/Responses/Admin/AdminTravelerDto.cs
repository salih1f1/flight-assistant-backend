namespace FlightAssistant.Contracts.Responses.Admin
{
    public record AdminTravelerDto(
        Guid Id,
        string Email,
        string? DisplayName,
        string? CountryCode,
        DateTime CreatedUtc
    );
}
