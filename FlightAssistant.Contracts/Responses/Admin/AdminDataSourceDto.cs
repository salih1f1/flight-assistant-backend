namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="Status">Derived by the API, not by the caller: the Degraded/Unhealthy line is
    /// drawn in one place (<c>DataSourceStatusEvaluator</c>) so the panel renders rather than
    /// re-derives.</param>
    /// <param name="WhatIsChecked">Rendered verbatim. The answer to "green — but green for what?"</param>
    /// <param name="LastError">Sanitized; exception-derived detail is non-Production only.</param>
    public sealed record AdminDataSourceDto(
        string Key,
        string DisplayName,
        string Kind,
        string Criticality,
        string Status,
        string WhatIsChecked,
        string? ExternalUrl,
        string? LastOutcome,
        DateTime? LastOutcomeUtc,
        DateTime? LastOkUtc,
        string? LastError,
        int ConsecutiveFailures,
        bool CanProbe,
        string? ProbeCostNote,
        DateTime? LastProbeUtc,
        string? LastProbeBy);
}
