namespace FlightAssistant.Application.Providers
{
    /// <param name="IsDefault">True when no row exists and these providers came from configuration.
    /// The panel renders "default" rather than implying an operator chose it.</param>
    public sealed record ProviderRouteState(
        string Job,
        IReadOnlyList<string> Providers,
        bool IsDefault,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason);
}
