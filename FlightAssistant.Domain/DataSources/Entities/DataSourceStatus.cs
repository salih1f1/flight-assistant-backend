namespace FlightAssistant.Domain.DataSources.Entities
{
    /// <summary>
    /// The current observed state of one outside dependency — one row per catalog key.
    ///
    /// <para>
    /// Deliberately a flat, mutable row rather than a rich domain entity: it is diagnostics written
    /// from a buffer on a background flush, not a business invariant. <c>LastOutcome</c> is stored
    /// as its enum *name* so a psql session can read it, matching <c>verification_requests</c>.
    /// </para>
    /// </summary>
    public sealed class DataSourceStatus
    {
        public string Key { get; set; } = string.Empty;

        public string? LastOutcome { get; set; }

        public DateTime? LastOutcomeUtc { get; set; }

        /// <summary>Most recent Ok. Backs the Degraded/Unhealthy split.</summary>
        public DateTime? LastOkUtc { get; set; }

        /// <summary>Sanitized per the spec's §2.2 rule. Never an upstream exception's message.</summary>
        public string? LastError { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTime? LastProbeUtc { get; set; }

        public string? LastProbeBy { get; set; }
    }
}
