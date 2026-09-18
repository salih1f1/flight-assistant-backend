namespace FlightAssistant.Contracts.Requests.Admin
{
    /// <summary>Body of <c>PUT /admin/jobs/{job}</c>.</summary>
    /// <param name="Reason">Optional operator note. Stored on the switch row and folded into the
    /// audit row's summary, so a later reader can see why, not just what.</param>
    public sealed record ToggleJobRequest(bool Enabled, string? Reason = null);
}
