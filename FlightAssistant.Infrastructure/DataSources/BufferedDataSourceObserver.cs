using System.Collections.Concurrent;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Domain.DataSources.Entities;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>
    /// Holds the current state of every source in memory and hands the changed ones to
    /// <c>DataSourceFlushService</c> to persist.
    ///
    /// <para>
    /// <b>Why buffer at all.</b> Recording per call would mean one database write per outbound HTTP
    /// call the application makes. The policy instead writes immediately on anything an operator
    /// would want to see at once — a changed outcome, or any non-Ok outcome — and coalesces a run of
    /// identical Ok observations to at most one write per source per
    /// <see cref="CoalesceWindow"/>. A healthy, busy source therefore costs about twelve writes an
    /// hour, and a newly-failing one is visible on the next flush.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws.</b> An observation is diagnostics. A failure to record one must not fail
    /// the call it was describing, so every entry point swallows bad input rather than validating it.
    /// </para>
    /// </summary>
    public sealed class BufferedDataSourceObserver : IDataSourceObserver
    {
        /// <summary>How long a run of identical Ok observations is collapsed into one write.</summary>
        public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMinutes(5);

        private readonly TimeProvider _timeProvider;
        private readonly ConcurrentDictionary<string, State> _byKey = new(StringComparer.Ordinal);

        public BufferedDataSourceObserver(TimeProvider timeProvider) => _timeProvider = timeProvider;

        private sealed class State
        {
            public DataSourceOutcome? LastOutcome;
            public DateTimeOffset LastOutcomeUtc;
            public DateTimeOffset? LastOkUtc;
            public string? LastError;
            public int ConsecutiveFailures;
            public DateTimeOffset? LastWrittenUtc;
            public bool Dirty;
        }

        public void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            var state = _byKey.GetOrAdd(key, _ => new State());

            lock (state)
            {
                var changed = state.LastOutcome != outcome;

                state.LastOutcome = outcome;
                state.LastOutcomeUtc = at;

                if (outcome == DataSourceOutcome.Ok)
                {
                    state.LastOkUtc = at;
                    state.ConsecutiveFailures = 0;
                    state.LastError = null;
                }
                else if (outcome is DataSourceOutcome.Failed or DataSourceOutcome.Unauthorized)
                {
                    state.ConsecutiveFailures++;
                    state.LastError = detail;
                }

                // Write at once on a change or on anything that is not Ok; otherwise only once the
                // coalescing window has elapsed since the last write.
                var mustWrite =
                    changed
                    || outcome != DataSourceOutcome.Ok
                    || state.LastWrittenUtc is null
                    || at - state.LastWrittenUtc >= CoalesceWindow;

                if (mustWrite) state.Dirty = true;
            }
        }

        /// <summary>
        /// The rows that need persisting, marking them clean. Called by the flush service and on
        /// graceful shutdown; a hard kill loses at most one coalescing window of "still fine",
        /// which is the only information this policy is willing to drop.
        /// </summary>
        public IReadOnlyCollection<DataSourceStatus> TakePending()
        {
            var pending = new List<DataSourceStatus>();
            var now = _timeProvider.GetUtcNow();

            foreach (var (key, state) in _byKey)
            {
                lock (state)
                {
                    if (!state.Dirty) continue;

                    pending.Add(new DataSourceStatus
                    {
                        Key = key,
                        LastOutcome = state.LastOutcome?.ToString(),
                        LastOutcomeUtc = state.LastOutcomeUtc.UtcDateTime,
                        LastOkUtc = state.LastOkUtc?.UtcDateTime,
                        LastError = state.LastError,
                        ConsecutiveFailures = state.ConsecutiveFailures,
                    });

                    state.Dirty = false;
                    state.LastWrittenUtc = now;
                }
            }

            return pending;
        }
    }
}
