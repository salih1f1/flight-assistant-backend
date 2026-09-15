namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>
    /// Download-once, reuse-for-at-most-24h, release-on-demand cache for one verification source's
    /// parsed dataset (#367 task 3, spec §4.3). Shared by <see cref="OurAirportsVerificationSource"/>
    /// and <see cref="MledozeCountriesVerificationSource"/> so the download/age/release bookkeeping
    /// exists in one place instead of twice.
    ///
    /// <para>
    /// <b>Thread-safety.</b> The job processes requests sequentially today, but this cache is still
    /// safe against two lookups racing to be the first to need the dataset: only the first caller to
    /// find the cache empty (or stale) actually invokes <c>load</c>; a caller that arrives while that
    /// download is still in flight awaits the very same <see cref="Task{T}"/> rather than starting a
    /// second one.
    /// </para>
    ///
    /// <para>
    /// A failed load is never cached "sticky" -- it is evicted the moment it fails, so the very next
    /// call retries immediately rather than waiting out the rest of a 24h window on one transient
    /// failure.
    /// </para>
    /// </summary>
    internal sealed class VerificationDatasetCache<T> where T : class
    {
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _maxAge;
        private readonly object _gate = new();
        private Task<T>? _loadTask;
        private DateTimeOffset _loadedAtUtc;

        public VerificationDatasetCache(TimeProvider timeProvider, TimeSpan maxAge)
        {
            _timeProvider = timeProvider;
            _maxAge = maxAge;
        }

        /// <summary>
        /// Returns the cached dataset, calling <paramref name="load"/> to (re)download and parse it
        /// only when nothing is cached, the cached copy is at least <c>maxAge</c> old, or a prior
        /// load failed. <paramref name="ct"/> is passed through to <paramref name="load"/> only when
        /// this call is the one that actually starts a new load -- a caller that instead joins an
        /// already in-flight download is bound by that download's own cancellation, not its own.
        /// </summary>
        public Task<T> GetAsync(Func<CancellationToken, Task<T>> load, CancellationToken ct)
        {
            Task<T> task;
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                if (_loadTask is null || now - _loadedAtUtc >= _maxAge)
                {
                    _loadedAtUtc = now;
                    _loadTask = load(ct);
                }

                task = _loadTask;
            }

            return AwaitAndEvictOnFailureAsync(task);
        }

        /// <summary>Drops the cached dataset, if any, so the next <see cref="GetAsync"/> call
        /// downloads again. Safe to call when nothing is cached.</summary>
        public void Release()
        {
            lock (_gate)
            {
                _loadTask = null;
            }
        }

        private async Task<T> AwaitAndEvictOnFailureAsync(Task<T> task)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch
            {
                // Only evict if this failed task is still the current one -- a concurrent successful
                // reload (after a Release()) must not be clobbered by a straggling failure.
                lock (_gate)
                {
                    if (ReferenceEquals(_loadTask, task)) _loadTask = null;
                }

                throw;
            }
        }
    }
}
