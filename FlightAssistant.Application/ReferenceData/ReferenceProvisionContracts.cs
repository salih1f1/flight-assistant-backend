using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.ReferenceData
{
    /// <summary>One reference row a caller could not resolve: its type and the code it looked it
    /// up by (an IATA code for an airport or airline, a two-letter code for a country).</summary>
    public sealed record MissingReference(VerificationEntityType Type, string Code);

    /// <summary>What a provisioning attempt achieved (#465).</summary>
    public enum ReferenceProvisionStatus
    {
        /// <summary>Every missing row now exists. The caller re-resolves its ids and continues.</summary>
        Provisioned,

        /// <summary>A source answered, and it has no such entity. The only outcome that may become a
        /// 404 -- the caller genuinely learned the entity does not exist.</summary>
        NotFound,

        /// <summary>We could not find out: a source failed, the budget held the call back, the
        /// wall-clock deadline ran out, or the source is not registered. Becomes a 503, never a 404.</summary>
        Unavailable
    }

    /// <summary>
    /// The outcome of one <see cref="IReferenceDataProvisioner.ProvisionAsync"/> call, naming the
    /// entity that failed so the caller's 404 or 503 can say which one it was.
    /// </summary>
    public sealed record ReferenceProvisionResult(
        ReferenceProvisionStatus Status,
        VerificationEntityType? FailedType,
        string? FailedCode,
        string? DiagnosticDetail)
    {
        public static readonly ReferenceProvisionResult Provisioned =
            new(ReferenceProvisionStatus.Provisioned, null, null, null);

        public static ReferenceProvisionResult NotFound(VerificationEntityType type, string code) =>
            new(ReferenceProvisionStatus.NotFound, type, code, null);

        /// <param name="diagnosticDetail">An operator-facing cause -- a source's own fixed-category
        /// error, or the budget state. Surfaced outside Production only, per the
        /// <c>DomainException.DiagnosticDetail</c> convention, so it must never carry a credential
        /// or a request URL.</param>
        public static ReferenceProvisionResult Unavailable(
            VerificationEntityType type, string code, string? diagnosticDetail) =>
            new(ReferenceProvisionStatus.Unavailable, type, code, diagnosticDetail);
    }

    /// <summary>
    /// Creates reference rows that are missing locally but exist in the external sources we already
    /// use for verification (#465), so <c>POST /flights</c> can track a flight through an airport or
    /// on an airline nobody has imported yet, and 404 means the entity genuinely does not exist.
    /// </summary>
    public interface IReferenceDataProvisioner
    {
        /// <summary>
        /// Resolves every entry in <paramref name="missing"/>, plus any country a resolved airport or
        /// airline turns out to need, and writes them in one save. Never throws for an expected
        /// failure -- a source error, a miss, a budget refusal and a timeout are all statuses.
        /// </summary>
        Task<ReferenceProvisionResult> ProvisionAsync(
            IReadOnlyCollection<MissingReference> missing, CancellationToken ct = default);
    }
}
