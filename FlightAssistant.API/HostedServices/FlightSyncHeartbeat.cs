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
        string? LastError,
        // How long the last completed run took (#353). Null until one completes — a run that has
        // never finished must not read as an instant one. Measured by the background service with
        // its own TimeProvider, so this stays a plain state holder with no clock.
        TimeSpan? LastRunDuration = null);

    /// <summary>
    /// Process-wide observable state for <see cref="FlightStatusSyncBackgroundService"/>. Both the
    /// writer (the background service) and the reader (the admin health endpoint, #311 task 2) live
    /// in the API layer, so this is a concrete singleton rather than an interface behind a
    /// cross-layer abstraction.
    ///
    /// <para>
    /// Readers need no locking of their own: <see cref="FlightSyncSnapshot"/> is an immutable record,
    /// and .NET guarantees that reading or replacing a reference-typed field is atomic, so
    /// <see cref="Current"/> always returns one complete, self-consistent snapshot — never a mix of an
    /// old one and a new one. The <c>lock</c> below is for writers: it serialises the read-modify-write
    /// inside each <c>Mark*</c> method so a second writer, if one is ever added, cannot race the first
    /// and silently lose its update. There is exactly one writer today (the background service's
    /// sequential loop), so nothing currently races on it — the lock is insurance against a second
    /// writer showing up later, not a requirement for today's single-writer correctness.
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
        /// flights, not the sync loop. <paramref name="duration"/> is how long the run took,
        /// measured by the caller's clock.
        /// </summary>
        public void MarkRunSucceeded(int processed, int failed, DateTime nextRunDueUtc, TimeSpan? duration = null)
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
                    LastRunDuration = duration,
                };
            }
        }

        /// <summary>
        /// Marks the whole run (not a single flight) as failed. Records only the exception's type
        /// and message — never a stack trace, since this value is rendered to an operator outside
        /// Production. A failed run's duration is still worth reporting — a run that failed after
        /// ten minutes is a different problem from one that failed at once.
        /// </summary>
        public void MarkRunFailed(Exception ex, DateTime nextRunDueUtc, TimeSpan? duration = null)
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunCompletedUtc = DateTime.UtcNow,
                    LastOutcome = FlightSyncOutcome.Failed,
                    NextRunDueUtc = nextRunDueUtc,
                    LastError = $"{ex.GetType().Name}: {ex.Message}",
                    LastRunDuration = duration,
                };
            }
        }
    }
}
