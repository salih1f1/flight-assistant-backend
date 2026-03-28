namespace FlightAssistant.Contracts.Responses.Admin
{
    public record CountryAdminDto(
        string Code,
        string Name,
        string? Alpha3,
        int? NumericCode,
        string? Region,
        string? Subregion,
        bool? IsEU,
        bool? IsSchengen,
        bool? IsEEA
    );
}
