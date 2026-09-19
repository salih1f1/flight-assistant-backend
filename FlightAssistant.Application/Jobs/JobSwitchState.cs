namespace FlightAssistant.Application.Jobs
{
    /// <param name="Enabled">The runtime switch alone. It never encodes the config ceiling —
    /// callers combine the two, so neither value can be mistaken for the other.</param>
    public sealed record JobSwitchState(
        string Job,
        bool Enabled,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason);
}
