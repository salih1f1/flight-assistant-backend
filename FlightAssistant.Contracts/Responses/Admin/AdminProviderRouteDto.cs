namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="Providers">The stored row's providers, or the configured default when there is no
    /// row — the one value <paramref name="IsDefault"/>, <paramref name="ChangedUtc"/>,
    /// <paramref name="ChangedBy"/> and <paramref name="Reason"/> all describe. Deliberately not
    /// collapsed into <paramref name="EffectiveProviders"/>: on the documented rollback path (a row
    /// says <c>aerodatabox</c>, then <c>AeroDataBox:ApiKey</c> is pulled) reporting the ceiling's
    /// providers here would put configuration's choice beside a named admin's
    /// <paramref name="ChangedBy"/> and <paramref name="Reason"/> with
    /// <paramref name="IsDefault"/> false — reading as though that admin had chosen the provider
    /// configuration forced.</param>
    /// <param name="EffectiveProviders">The providers actually in force right now: the same as
    /// <paramref name="Providers"/> unless configuration ceilings this key, in which case it is the
    /// configured default the ceiling forces whatever the stored row says. Derived here so the panel
    /// renders one answer rather than combining two and risking a different rule — the same
    /// reasoning, and the same keep-both-and-derive-the-third shape, as
    /// <c>AdminJobDto.EffectiveEnabled</c>.</param>
    /// <param name="IsDefault">True when no row exists and <paramref name="Providers"/> comes from
    /// configuration.</param>
    /// <param name="CanChange">False when configuration bars every alternative — for
    /// <c>flight-search-route</c>, when <c>AeroDataBox:ApiKey</c> is unset. The panel disables the
    /// control and shows why rather than hiding it, matching <c>AdminJobDto.CanToggle</c>.</param>
    public sealed record AdminProviderRouteDto(
        string Job,
        string DisplayName,
        IReadOnlyList<string> Providers,
        IReadOnlyList<string> EffectiveProviders,
        IReadOnlyList<string> Options,
        bool IsDefault,
        bool CanChange,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason);
}
