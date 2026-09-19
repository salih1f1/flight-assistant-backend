namespace FlightAssistant.Application.Interfaces.Providers
{
    /// <summary>
    /// The configured default for a routing key — what applies when no row exists and what a failed
    /// read falls back to. An interface rather than an <c>IConfiguration</c> read inside
    /// <c>CachedProviderRouting</c> because each key's default comes from a different place:
    /// <c>flight-search-route</c> from whether <c>AeroDataBox:ApiKey</c> is set, the verification
    /// keys (PR 2) from <c>Verification:DefaultSources:*</c>.
    /// </summary>
    public interface IProviderRouteDefaults
    {
        IReadOnlyList<string> For(string job);

        /// <summary>
        /// True when configuration leaves this key no real choice — for
        /// <c>flight-search-route</c>, when <c>AeroDataBox:ApiKey</c> is unset. A write against a
        /// ceilinged key is refused, matching how <c>ToggleJobCommand</c> refuses a job whose config
        /// has it off.
        /// </summary>
        bool IsCeilinged(string job);
    }
}
