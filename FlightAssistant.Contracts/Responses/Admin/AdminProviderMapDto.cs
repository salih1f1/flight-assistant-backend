namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="RoutingKey">Null when this operation's provider is fixed.</param>
    /// <param name="FixedReason">Why it is fixed, or null when it is switchable.</param>
    /// <param name="Note">A consequence to read before acting on this row. Unlike
    /// <paramref name="FixedReason"/>, a switchable row can carry one.</param>
    public sealed record AdminProviderOperationDto(
        string Trigger,
        string Operation,
        IReadOnlyList<string> Providers,
        string? RoutingKey,
        string? FixedReason,
        string ListPrice,
        string? Note);

    /// <param name="QuotaRemaining">Null means unknown — this process has made no call yet — never
    /// "nothing left". Per instance, so a scaled-out deployment shows whichever instance answered.</param>
    /// <param name="ObservedUtc">When the figure was read. Null with a null quota.</param>
    /// <param name="BudgetState">"Normal", "SoftLimit" or "Exhausted" for a metered provider; null
    /// for a free one.</param>
    public sealed record AdminProviderHeadroomDto(
        string Provider,
        string? BudgetState,
        decimal? SpentUsd,
        decimal? BudgetUsd,
        int? QuotaRemaining,
        int? QuotaLimit,
        DateTimeOffset? ObservedUtc);

    /// <summary>
    /// What <c>GET /admin/providers</c> returns: the switchable decisions, the full call map
    /// including fixed rows, and each provider's remaining headroom.
    /// </summary>
    public sealed record AdminProviderMapDto(
        IReadOnlyList<AdminProviderRouteDto> Routes,
        IReadOnlyList<AdminProviderOperationDto> Operations,
        IReadOnlyList<AdminProviderHeadroomDto> Headroom);
}
