namespace FlightAssistant.Contracts.Requests.Admin
{
    /// <summary>Body of <c>PUT /admin/providers/{job}</c>.</summary>
    /// <param name="Providers">The chosen providers, in the order they should be asked. One name for
    /// <c>flight-search-route</c>; a verification key may name several.</param>
    /// <param name="Reason">Optional operator note. Stored on the row and folded into the audit
    /// row's summary, so a later reader can see why, not just what.</param>
    public sealed record SetProviderRouteRequest(IReadOnlyList<string> Providers, string? Reason = null);
}
