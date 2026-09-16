namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One column that differed for one source, from a verification check's stored
    /// <c>differences</c> (#385, R2).</summary>
    public sealed record VerificationRequestDifferenceDto(string Column, string? Ours, string? Source, string SourceName);

    /// <summary>
    /// One row of <c>GET /admin/verification-requests</c>: one verification check of one entity
    /// (#385). <see cref="EntityType"/>, <see cref="Trigger"/> and <see cref="Status"/> are the
    /// underlying enum member's name (e.g. <c>"Airport"</c>, <c>"Admin"</c>, <c>"Mismatch"</c>).
    /// <see cref="Differences"/> and <see cref="SourceValues"/> come back typed (R2), not as the
    /// stored JSON strings: <see cref="SourceValues"/> is keyed by source name, each value a map of
    /// column name to the value that source reported.
    /// </summary>
    public sealed record VerificationRequestDto(
        long Id,
        string EntityType,
        string EntityKey,
        string Sources,
        string Trigger,
        string Status,
        int Attempts,
        DateTime? NextAttemptUtc,
        IReadOnlyList<VerificationRequestDifferenceDto> Differences,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> SourceValues,
        string? Error,
        decimal EstimatedCostUsd,
        string RequestedBy,
        DateTime RequestedUtc,
        DateTime? StartedUtc,
        DateTime? CompletedUtc);
}
