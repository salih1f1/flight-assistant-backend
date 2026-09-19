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
    public sealed record ReferenceProvisionResult
    {
        /// <summary>
        /// Private so the three factories below are the <b>only</b> way to build one (#510). This
        /// was a public positional constructor, which meant
        /// <c>new ReferenceProvisionResult(ReferenceProvisionStatus.NotFound, null, null, null)</c>
        /// compiled -- a <c>NotFound</c> naming no entity. Nothing built one, because the factories
        /// were already the only path anything used, but <c>TrackFlightCommandHandler</c>'s
        /// <c>ToDomainException</c> dereferences <c>FailedType!.Value</c> and <c>FailedCode!</c> on
        /// exactly that branch, so such a result would have been a <c>NullReferenceException</c>
        /// inside <c>POST /flights</c>. The <c>!</c> there is now provably correct rather than
        /// correct by convention.
        ///
        /// <para>
        /// The properties are get-only rather than <c>init</c> for the same reason: an <c>init</c>
        /// setter would let a <c>with</c> expression rebuild the type into a state the factories
        /// refuse, which is the hole this closes from the other side. Value equality, which the
        /// tests rely on, is unaffected -- it comes from being a record, not from the constructor's
        /// accessibility.
        /// </para>
        /// </summary>
        private ReferenceProvisionResult(
            ReferenceProvisionStatus status,
            VerificationEntityType? failedType,
            string? failedCode,
            string? diagnosticDetail)
        {
            Status = status;
            FailedType = failedType;
            FailedCode = failedCode;
            DiagnosticDetail = diagnosticDetail;
        }

        public ReferenceProvisionStatus Status { get; }

        /// <summary>The entity that failed. Non-null for <c>NotFound</c> and <c>Unavailable</c>,
        /// null for <c>Provisioned</c> -- guaranteed by the factories being the only constructor.</summary>
        public VerificationEntityType? FailedType { get; }

        /// <summary>The code that entity was looked up by. Non-null exactly when
        /// <see cref="FailedType"/> is.</summary>
        public string? FailedCode { get; }

        public string? DiagnosticDetail { get; }

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
