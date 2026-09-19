namespace FlightAssistant.Domain.Jobs.Entities
{
    /// <summary>
    /// One background job's runtime on/off switch. Persisted rather than held in memory so a
    /// toggle survives a restart: a deploy silently re-enabling a job an operator stopped is the
    /// failure they would least expect and least quickly notice.
    /// </summary>
    public sealed class JobSwitch
    {
        public string Job { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public DateTime? ChangedUtc { get; set; }

        public string? ChangedBy { get; set; }

        /// <summary>Optional operator note, rendered on the health page beside the state.</summary>
        public string? Reason { get; set; }
    }
}
