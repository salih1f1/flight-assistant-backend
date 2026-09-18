namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="ConfigEnabled">The deployment ceiling. False means this job cannot run in this
    /// environment at all and no toggle can start it.</param>
    /// <param name="SwitchEnabled">The runtime switch alone.</param>
    /// <param name="EffectiveEnabled"><c>ConfigEnabled &amp;&amp; SwitchEnabled</c>. Derived here so
    /// the panel renders one answer rather than combining two and risking a different rule.</param>
    /// <param name="CanToggle">False exactly when <paramref name="ConfigEnabled"/> is false. The
    /// panel disables the control and shows why, rather than hiding it — a missing control makes an
    /// operator hunt for a feature that is present but barred.</param>
    /// <param name="LastOutcome">The owning heartbeat's <c>LastOutcome</c> (e.g. "Success", "Failed",
    /// "SwitchedOff"), as a name rather than a number for the same reason every other admin DTO in
    /// this codebase renders an enum as a string. Filled in by <c>AdminJobEndpoints</c>, not by
    /// either Application handler — see their shared remarks on why.</param>
    /// <param name="LastRunUtc">When the job last finished a run, or started one if none has ever
    /// finished — never null-means-broken: a job that has genuinely never run reads as
    /// <c>LastOutcome</c> "NeverRun" with this null, not as a missing timestamp.</param>
    public sealed record AdminJobDto(
        string Job,
        string DisplayName,
        bool ConfigEnabled,
        bool SwitchEnabled,
        bool EffectiveEnabled,
        bool CanToggle,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason,
        string? LastOutcome,
        DateTime? LastRunUtc);
}
