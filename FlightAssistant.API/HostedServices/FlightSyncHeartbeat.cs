namespace FlightAssistant.API.HostedServices
{
    public enum FlightSyncOutcome { NeverRun, Running, Success, Failed }

    public sealed record FlightSyncSnapshot(
        DateTime? LastRunStartedUtc,
        DateTime? LastRunCompletedUtc,
        FlightSyncOutcome LastOutcome,
        int FlightsProcessed,
        int FlightsFailed,
        DateTime? NextRunDueUtc,
        string? LastError);

    /// <summary>
    /// Process-wide observable state for <see cref="FlightStatusSyncBackgroundService"/>. Both the
    /// writer (the background service) and the reader (the admin health endpoint, #311 task 2) live
    /// in the API layer, so this is a concrete singleton rather than an interface behind a
    /// cross-layer abstraction.
    ///
    /// <para>
    /// The snapshot is read and replaced as a whole under a lock, never through individual mutable
    /// fields. A reader that could observe fields mid-update would risk a torn report — e.g. a fresh
    /// <see cref="FlightSyncOutcome.Success"/> sitting beside the previous run's counts.
    /// </para>
    /// </summary>
    public sealed class FlightSyncHeartbeat
    {
        private readonly object _lock = new();
        private FlightSyncSnapshot _snapshot = new(
            LastRunStartedUtc: null,
            LastRunCompletedUtc: null,
            LastOutcome: FlightSyncOutcome.NeverRun,
            FlightsProcessed: 0,
            FlightsFailed: 0,
            NextRunDueUtc: null,
            LastError: null);

        /// <summary>The current snapshot. Safe to read from any thread.</summary>
        public FlightSyncSnapshot Current
        {
            get
            {
                lock (_lock)
                {
                    return _snapshot;
                }
            }
        }

        /// <summary>
        /// Marks the start of a new run. Leaves the previous run's completion timestamp and counts
        /// alone until this run finishes — a run in flight should not blank out what the last one
        /// reported.
        /// </summary>
        public void MarkRunStarted()
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunStartedUtc = DateTime.UtcNow,
                    LastOutcome = FlightSyncOutcome.Running,
                };
            }
        }

        /// <summary>
        /// Marks a run as completed. A batch that processed every flight but had some throw is
        /// still <see cref="FlightSyncOutcome.Success"/> — the batch itself ran to completion;
        /// <paramref name="failed"/> is what tells the operator something is wrong with individual
        /// flights, not the sync loop.
        /// </summary>
        public void MarkRunSucceeded(int processed, int failed, DateTime nextRunDueUtc)
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunCompletedUtc = DateTime.UtcNow,
                    LastOutcome = FlightSyncOutcome.Success,
                    FlightsProcessed = processed,
                    FlightsFailed = failed,
                    NextRunDueUtc = nextRunDueUtc,
                    LastError = null,
                };
            }
        }

        /// <summary>
        /// Marks the whole run (not a single flight) as failed. Records only the exception's type
        /// and message — never a stack trace, since this value is rendered to an operator outside
        /// Production.
        /// </summary>
        public void MarkRunFailed(Exception ex, DateTime nextRunDueUtc)
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunCompletedUtc = DateTime.UtcNow,
                    LastOutcome = FlightSyncOutcome.Failed,
                    NextRunDueUtc = nextRunDueUtc,
                    LastError = $"{ex.GetType().Name}: {ex.Message}",
                };
            }
        }
    }
}
