namespace FlightAssistant.API.HostedServices
{
    public enum VerificationJobOutcome { NeverRun, Running, Success, Failed }

    public sealed record VerificationJobSnapshot(
        DateTime? LastRunStartedUtc,
        DateTime? LastRunCompletedUtc,
        VerificationJobOutcome LastOutcome,
        int RequestsProcessed,
        int RequestsFailed,
        DateTime? NextRunDueUtc,
        string? LastError);

    /// <summary>
    /// Process-wide observable state for <see cref="ReferenceVerificationBackgroundService"/>, read by
    /// the admin health report (#367). Same design as <see cref="FlightSyncHeartbeat"/>: the writer and
    /// the reader both live in the API layer, so this is a concrete singleton, and each <c>Mark*</c>
    /// call publishes a new immutable <see cref="VerificationJobSnapshot"/> by one atomic reference
    /// write, so a reader always sees one whole snapshot. The lock serialises the read-modify-write
    /// inside each <c>Mark*</c> method in case a second writer is ever added.
    /// </summary>
    public sealed class VerificationJobHeartbeat
    {
        private readonly object _lock = new();
        private readonly TimeProvider _timeProvider;
        private VerificationJobSnapshot _snapshot = new(
            LastRunStartedUtc: null,
            LastRunCompletedUtc: null,
            LastOutcome: VerificationJobOutcome.NeverRun,
            RequestsProcessed: 0,
            RequestsFailed: 0,
            NextRunDueUtc: null,
            LastError: null);

        public VerificationJobHeartbeat(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        /// <summary>The current snapshot. Safe to read from any thread.</summary>
        public VerificationJobSnapshot Current
        {
            get
            {
                lock (_lock)
                {
                    return _snapshot;
                }
            }
        }

        /// <summary>Marks the start of a cycle, leaving the previous cycle's completion time and
        /// counts in place until this one finishes.</summary>
        public void MarkRunStarted()
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunStartedUtc = UtcNow(),
                    LastOutcome = VerificationJobOutcome.Running,
                };
            }
        }

        /// <summary>
        /// Marks a cycle that ran to the end. A cycle in which some requests failed is still
        /// <see cref="VerificationJobOutcome.Success"/>: the loop is healthy, and
        /// <paramref name="failed"/> is what tells the operator that requests are failing.
        /// </summary>
        /// <param name="processed">Requests the cycle claimed and worked through.</param>
        /// <param name="failed">The subset that ended in a source error or <c>Error</c>.</param>
        public void MarkRunSucceeded(int processed, int failed, DateTime nextRunDueUtc)
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunCompletedUtc = UtcNow(),
                    LastOutcome = VerificationJobOutcome.Success,
                    RequestsProcessed = processed,
                    RequestsFailed = failed,
                    NextRunDueUtc = nextRunDueUtc,
                    LastError = null,
                };
            }
        }

        /// <summary>
        /// Marks the whole cycle (not one request) as failed. Records only the exception's type and
        /// message, never a stack trace, since the value is shown to an operator outside Production.
        /// </summary>
        public void MarkRunFailed(Exception ex, DateTime nextRunDueUtc)
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunCompletedUtc = UtcNow(),
                    LastOutcome = VerificationJobOutcome.Failed,
                    NextRunDueUtc = nextRunDueUtc,
                    LastError = $"{ex.GetType().Name}: {ex.Message}",
                };
            }
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
    }
}
