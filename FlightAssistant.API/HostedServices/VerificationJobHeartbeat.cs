namespace FlightAssistant.API.HostedServices
{
    /// <summary><c>Paused</c> (#401 task 2, R4/R5) is a cycle that worked through its batch but left
    /// some requests Pending because this month's AeroAPI budget is exhausted -- distinct from
    /// <c>Success</c> (nothing held back) and from <c>Failed</c> (the cycle itself broke), so an
    /// operator sees *why* the queue isn't draining without reading logs.</summary>
    // SwitchedOff (#471): an admin stopped this job from the panel. Distinct from Paused on the
    // verification job, which means the cycle RAN and held work back because the AeroAPI budget is
    // exhausted -- "we chose not to spend" and "an operator stopped us" need different responses.
    public enum VerificationJobOutcome { NeverRun, Running, Success, Paused, Failed, SwitchedOff }

    public sealed record VerificationJobSnapshot(
        DateTime? LastRunStartedUtc,
        DateTime? LastRunCompletedUtc,
        VerificationJobOutcome LastOutcome,
        int RequestsProcessed,
        int RequestsFailed,
        int StuckAbandoned,
        int FailuresUnrecorded,
        DateTime? NextRunDueUtc,
        string? LastError,
        int RequestsPaused = 0);

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
            StuckAbandoned: 0,
            FailuresUnrecorded: 0,
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
        /// <param name="stuckAbandoned">Stuck <c>Running</c> requests this cycle's reset gave up on,
        /// completing them as <c>Error</c> because they had no attempts left.</param>
        /// <param name="failuresUnrecorded">The subset of <paramref name="failed"/> whose failure
        /// couldn't even be recorded: still <c>Running</c>, with no attempt counted until the next
        /// stuck-request reset.</param>
        public void MarkRunSucceeded(int processed, int failed, int stuckAbandoned, int failuresUnrecorded, DateTime nextRunDueUtc)
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunCompletedUtc = UtcNow(),
                    LastOutcome = VerificationJobOutcome.Success,
                    RequestsProcessed = processed,
                    RequestsFailed = failed,
                    StuckAbandoned = stuckAbandoned,
                    FailuresUnrecorded = failuresUnrecorded,
                    RequestsPaused = 0,
                    NextRunDueUtc = nextRunDueUtc,
                    LastError = null,
                };
            }
        }

        /// <summary>
        /// Marks a cycle that worked through its batch but left some requests Pending because this
        /// month's AeroAPI budget is exhausted (#401 task 2, R4/R5): <see cref="VerificationJobOutcome.Paused"/>,
        /// not <see cref="VerificationJobOutcome.Success"/> (nothing was held back) and not
        /// <see cref="VerificationJobOutcome.Failed"/> (the cycle itself did not break -- it is
        /// deliberately declining to spend money it doesn't have). Otherwise identical to
        /// <see cref="MarkRunSucceeded"/>: every other outcome in the same batch (verified, mismatch,
        /// failed, lost) is still counted normally.
        /// </summary>
        /// <param name="paused">Requests this cycle left Pending untouched -- no attempt counted, no
        /// error recorded -- because they named the AeroAPI source while the budget guard reported
        /// <c>Exhausted</c>.</param>
        public void MarkRunPaused(int processed, int failed, int stuckAbandoned, int failuresUnrecorded, int paused, DateTime nextRunDueUtc)
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunCompletedUtc = UtcNow(),
                    LastOutcome = VerificationJobOutcome.Paused,
                    RequestsProcessed = processed,
                    RequestsFailed = failed,
                    StuckAbandoned = stuckAbandoned,
                    FailuresUnrecorded = failuresUnrecorded,
                    RequestsPaused = paused,
                    NextRunDueUtc = nextRunDueUtc,
                    LastError = null,
                };
            }
        }

        /// <summary>
        /// Marks the whole cycle (not one request) as failed before it finished its batch, keeping the
        /// previous cycle's counts. Records only the exception's type and message, never a stack trace,
        /// since the value is shown to an operator outside Production.
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

        /// <summary>
        /// As <see cref="MarkRunFailed(Exception, DateTime)"/>, for a cycle that failed only after
        /// working through its batch (releasing source caches threw): the batch's counts replace the
        /// previous cycle's, since they describe work this cycle really did.
        /// </summary>
        /// <param name="paused">The failed batch's own paused count (R4/R5) -- a cycle can pause some
        /// requests and then still fail releasing source caches; defaults to 0 for a caller with
        /// nothing to report.</param>
        public void MarkRunFailed(
            Exception ex, DateTime nextRunDueUtc, int processed, int failed, int stuckAbandoned, int failuresUnrecorded, int paused = 0)
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastRunCompletedUtc = UtcNow(),
                    LastOutcome = VerificationJobOutcome.Failed,
                    RequestsProcessed = processed,
                    RequestsFailed = failed,
                    StuckAbandoned = stuckAbandoned,
                    FailuresUnrecorded = failuresUnrecorded,
                    RequestsPaused = paused,
                    NextRunDueUtc = nextRunDueUtc,
                    LastError = $"{ex.GetType().Name}: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// Marks a tick that did no work because an admin switched this job off from the panel
        /// (#471). Every other field carries over unchanged: the counts and timestamps still
        /// describe the last real cycle. Distinct from <see cref="VerificationJobOutcome.Paused"/>,
        /// which means the cycle ran and held work back for budget -- an operator stop is a
        /// different situation needing a different remedy.
        /// </summary>
        public void MarkSwitchedOff()
        {
            lock (_lock)
            {
                _snapshot = _snapshot with
                {
                    LastOutcome = VerificationJobOutcome.SwitchedOff,
                };
            }
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
    }
}
