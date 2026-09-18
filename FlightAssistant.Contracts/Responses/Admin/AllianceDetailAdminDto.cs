namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One airline on the alliance detail drill-down (#457).</summary>
    public record AllianceAirlineDto(
        int Id,
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode
    );

    /// <summary>
    /// An alliance with the airlines that belong to it. Uncapped and with no truncation flag,
    /// unlike the traveler and flight drill-downs: this list is bounded by how many airlines exist
    /// in the alliance, not by how long the app has been running.
    /// </summary>
    public record AllianceDetailAdminDto(
        int Id,
        string Code,
        string Name,
        IReadOnlyList<AllianceAirlineDto> Airlines
    );
}
