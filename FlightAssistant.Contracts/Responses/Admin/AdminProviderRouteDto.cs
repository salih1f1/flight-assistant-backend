namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="IsDefault">True when no row exists and these providers come from configuration.</param>
    /// <param name="CanChange">False when configuration bars every alternative — for
    /// <c>flight-search-route</c>, when <c>AeroDataBox:ApiKey</c> is unset. The panel disables the
    /// control and shows why rather than hiding it, matching <c>AdminJobDto.CanToggle</c>.</param>
    /// <param name="FixedReason">Why this decision cannot be changed, or null when it can. Non-null
    /// only on the read-only map rows added in PR 3.</param>
    public sealed record AdminProviderRouteDto(
        string Job,
        string DisplayName,
        IReadOnlyList<string> Providers,
        IReadOnlyList<string> Options,
        bool IsDefault,
        bool CanChange,
        string? FixedReason,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason);
}
