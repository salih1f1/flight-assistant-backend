namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// Whether the API's own App Service is running, and whether Always On is enabled, as the
    /// admin panel's health section renders it (#530).
    /// </summary>
    /// <param name="State">Whether the resource could be READ. Distinct from
    /// <paramref name="SiteState"/>, which is what the answer was.</param>
    /// <param name="AlwaysOn">Null means unknown, which the panel must not treat as false: a
    /// warning drawn from a value we never read would have no evidence behind it.</param>
    public sealed record AppServiceStatusDto(
        string State,
        string? SiteName,
        string? SiteState,
        string? AvailabilityState,
        bool? Enabled,
        bool? AlwaysOn,
        int? InstanceCount,
        string? Message);
}
