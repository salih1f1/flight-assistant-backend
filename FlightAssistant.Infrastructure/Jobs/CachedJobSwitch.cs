using System.Collections.Concurrent;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.Jobs
{
    /// <summary>
    /// Reads one job's persisted switch row, keyed by a <see cref="JobNames"/> value; null means no
    /// row exists for that job -- nobody has ever touched its switch.
    ///
    /// <para>
    /// A bare delegate rather than a repository interface: this is one read of one row, not a family
    /// of queries, and that narrowness is what lets <see cref="CachedJobSwitch"/>'s tests substitute
    /// an in-memory function for a database -- those tests are about the caching policy wrapped
    /// around this read, not the read itself. <see cref="CachedJobSwitch"/>'s public constructor
    /// supplies the real implementation, which resolves a scoped <see cref="FlightAssistantDbContext"/>
    /// from <see cref="IServiceScopeFactory"/> on every call, because the context is scoped and
    /// <see cref="CachedJobSwitch"/> is a singleton -- the same shape <c>DataSourceFlushService</c>
    /// uses for the same reason.
    /// </para>
    /// </summary>
    internal delegate Task<JobSwitchState?> JobSwitchReader(string job, CancellationToken ct);

    /// <summary>
    /// <see cref="IJobSwitch"/> backed by <c>job_switches</c>. <see cref="IsEnabledAsync"/> is cached
    /// per job for <see cref="CacheWindow"/>; <see cref="GetAsync"/> is not -- see its own remarks.
    /// </summary>
    public sealed class CachedJobSwitch : IJobSwitch
    {
        /// <summary>
        /// How long a read is trusted before the next call re-reads the database. Two background
        /// loops call <see cref="IsEnabledAsync"/> every tick -- every minute for flight sync -- to
        /// read a value that changes maybe twice a month, so an uncached read would be a database
        /// round trip per tick forever. This window is also the slowest a toggle can take effect,
        /// which is the number an operator notices after pressing the button, so it is kept short
        /// rather than tuned for read volume.
        /// </summary>
        public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(10);

        private readonly JobSwitchReader _read;
        private readonly TimeProvider _time;
        private readonly ILogger<CachedJobSwitch> _logger;

        // Per-job cache entry. A ConcurrentDictionary needs no external lock: every read either hits
        // an existing entry or replaces it wholesale (never mutates one in place), so a torn read is
        // not possible and a rare duplicate database read on a race is harmless.
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

        public CachedJobSwitch(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<CachedJobSwitch> logger)
            : this(ReadFromDatabase(scopeFactory), time, logger)
        {
        }

        /// <summary>Test seam: substitutes <paramref name="read"/> for the real database read, so the
        /// caching policy can be proven without standing up a database.</summary>
        internal CachedJobSwitch(JobSwitchReader read, TimeProvider time, ILogger<CachedJobSwitch> logger)
        {
            _read = read;
            _time = time;
            _logger = logger;
        }

        /// <summary>The real <see cref="JobSwitchReader"/>: one scope, one row, per call. The
        /// singleton itself never holds a <see cref="FlightAssistantDbContext"/> -- only this
        /// short-lived scope does -- so it cannot become a captive dependency on a scoped service.</summary>
        private static JobSwitchReader ReadFromDatabase(IServiceScopeFactory scopeFactory) => async (job, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            var row = await db.JobSwitches.FindAsync([job], ct);
            return row is null ? null : new JobSwitchState(row.Job, row.Enabled, row.ChangedUtc, row.ChangedBy, row.Reason);
        };

        /// <inheritdoc/>
        public async Task<bool> IsEnabledAsync(string job, CancellationToken ct)
        {
            var now = _time.GetUtcNow();
            if (_cache.TryGetValue(job, out var cached) && now - cached.ReadUtc < CacheWindow)
                return cached.Enabled;

            // Sticky per job for this singleton's lifetime: true once ANY read of this job has ever
            // completed without throwing -- including one that found no row, which is a successful
            // read of "nobody has touched this switch", not a failure. That is what the fail-open vs.
            // fail-closed split below keys on; what that read said is irrelevant here. `cached` is
            // the zero-value CacheEntry (HasSucceeded false) when the job has never been read at all,
            // which is exactly the cold-start case this is meant to catch.
            var everSucceeded = cached.HasSucceeded;

            bool enabled;
            try
            {
                var state = await _read(job, ct);
                enabled = state?.Enabled ?? true; // no row yet -- nobody has touched this switch
                everSucceeded = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (everSucceeded)
                {
                    // FAIL OPEN, deliberately: a read of this job has worked before, so this is
                    // steady-state noise -- a database blip -- not a signal, and a stop switch must
                    // never let a blip stop an otherwise-healthy job. Cached like a real "enabled"
                    // answer, so the next real attempt happens when this entry ages out of
                    // CacheWindow, not on some separate retry schedule.
                    _logger.LogWarning(ex, "Failed to read job switch {Job}; leaving it enabled.", job);
                    enabled = true;
                }
                else
                {
                    // FAIL CLOSED: this is the first read ever attempted for this job, most likely a
                    // cold start racing DataSourceSeeder for a still-warming connection pool. Nothing
                    // has yet confirmed this job is meant to be running, so default to the outcome
                    // that costs nothing when wrong -- skipping one tick -- rather than the one that
                    // can spend real AeroAPI budget on a job an operator switched off before this
                    // instance came up. The next tick tries a real read again.
                    _logger.LogWarning(ex,
                        "Failed to read job switch {Job} before any prior read has succeeded; treating it as disabled.", job);
                    enabled = false;
                }
            }

            _cache[job] = new CacheEntry(enabled, now, everSucceeded);
            return enabled;
        }

        /// <inheritdoc/>
        public async Task<JobSwitchState> GetAsync(string job, CancellationToken ct)
        {
            // Deliberately NOT wrapped in IsEnabledAsync's fail-open handling above: this backs the
            // admin page reading its own state back right after a toggle, not a hot loop, so a read
            // failure here should surface as an error rather than silently render "enabled" for a row
            // that may not be.
            var state = await _read(job, ct);
            return state ?? new JobSwitchState(job, true, null, null, null);
        }

        /// <param name="HasSucceeded">Whether ANY read of this job has ever completed without
        /// throwing -- not whether this particular cached value came from one. Carried forward
        /// unchanged on a fail-closed/fail-open entry so a later read failure can still tell a
        /// cold start (never succeeded) from a steady-state blip (succeeded at least once).</param>
        private readonly record struct CacheEntry(bool Enabled, DateTimeOffset ReadUtc, bool HasSucceeded);
    }
}
