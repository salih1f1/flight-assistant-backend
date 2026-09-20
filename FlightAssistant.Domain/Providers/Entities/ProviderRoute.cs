namespace FlightAssistant.Domain.Providers.Entities
{
    /// <summary>
    /// One routing decision's runtime value. Persisted rather than held in memory so a change
    /// survives a restart, for the same reason <c>JobSwitch</c> is: a deploy silently reverting a
    /// routing decision an operator made is the failure they would least expect and least quickly
    /// notice.
    ///
    /// <para>
    /// A sibling of <c>job_switches</c> rather than a widening of it. That table's value is a
    /// boolean and <c>JobNames</c> states its closed list is what keeps it "two booleans with a
    /// config ceiling, not a feature-flag framework"; adding a nullable string column to carry a
    /// value would weaken both. Each registry stays closed on its own terms.
    /// </para>
    /// </summary>
    public sealed class ProviderRoute
    {
        public string Job { get; set; } = string.Empty;

        /// <summary>
        /// The chosen provider or source names, comma-joined in the order they are asked — the same
        /// grammar <c>VerificationDefaultSources</c> uses, validated by
        /// <c>VerificationRequest.TryJoinSources</c>. <c>flight-search-route</c> happens to hold a
        /// single name; nothing in this shape assumes it.
        /// </summary>
        public string Providers { get; set; } = string.Empty;

        public DateTime? ChangedUtc { get; set; }

        public string? ChangedBy { get; set; }

        /// <summary>Optional operator note, rendered beside the state and folded into the audit row.</summary>
        public string? Reason { get; set; }
    }
}
