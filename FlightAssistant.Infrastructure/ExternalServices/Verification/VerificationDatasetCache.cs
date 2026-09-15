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
    /// download is still in flight joins the very same <see cref="Task{T}"/> rather than starting a
    /// second one.
    /// </para>
    ///
    /// <para>
    /// <b>Per-caller cancellation (fix round 1, item 7).</b> The shared load runs on a token this
    /// cache owns -- <see cref="CancellationToken.None"/>, not any individual caller's -- because a
    /// caller's own cancellation must never abort the download out from under every other caller
    /// still joined on it. <see cref="VerificationSourceDownloader.DownloadAsync{T}"/>'s own linked
    /// timeout (started with that owned token) is what actually bounds the shared download. Each
    /// caller of <see cref="GetAsync"/> instead wraps its own wait in
    /// <see cref="Task.WaitAsync(CancellationToken)"/>: cancelling one caller's <c>ct</c> throws
    /// <see cref="OperationCanceledException"/> for that caller alone and leaves the shared download,
    /// and every other caller awaiting it, untouched.
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
        /// load failed. <paramref name="load"/> is always invoked with <see cref="CancellationToken.None"/>
        /// -- see the "Per-caller cancellation" remark on this type -- while <paramref name="ct"/> is
        /// used only to bound <b>this</b> call's own wait for the (possibly shared) result.
        /// </summary>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> is already cancelled
        /// -- checked up front (fix round 2, item 4) so an already-given-up caller can never be the
        /// one that starts a brand new download for everyone else.</exception>
        public Task<T> GetAsync(Func<CancellationToken, Task<T>> load, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            Task<T> task;
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                if (_loadTask is null || now - _loadedAtUtc >= _maxAge)
                {
                    _loadedAtUtc = now;
                    // Captured into the local `task` *before* attaching the eviction continuation:
                    // when `load` fails synchronously (a pre-faulted Task, common with a fake
                    // HttpMessageHandler in tests), ExecuteSynchronously below runs the eviction
                    // immediately and nulls `_loadTask` right here, inside this lock -- re-reading
                    // the field afterwards would hand this caller a null Task instead of the very
                    // one it just started.
                    task = load(CancellationToken.None);
                    _loadTask = task;
                    AttachEvictOnFailure(task);
                }
                else
                {
                    task = _loadTask;
                }
            }

            return task.WaitAsync(ct);
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

        /// <summary>
        /// Fires once, independently of how many callers are awaiting <paramref name="task"/> or how
        /// many of them cancel their own wait: evicts <paramref name="task"/> from the cache if it
        /// completes as faulted or cancelled, so the very next <see cref="GetAsync"/> call retries
        /// rather than replaying the same failure for up to 24h. Only evicts if <paramref name="task"/>
        /// is still the current entry -- a concurrent successful reload (after a <see cref="Release"/>)
        /// must not be clobbered by a straggling failure from an older, abandoned load.
        /// </summary>
        private void AttachEvictOnFailure(Task<T> task)
        {
            _ = task.ContinueWith(
                completed =>
                {
                    // Touching .Exception marks a fault "observed" (fix round 2, item 3a). Every
                    // caller reaches this task only through GetAsync's task.WaitAsync(ct) wrapper --
                    // a *different* Task object -- so if every one of those waits was itself
                    // cancelled before this one faults, nothing else would ever read .Exception, and
                    // this Task would raise TaskScheduler.UnobservedTaskException once GC'd.
                    if (completed.IsFaulted) _ = completed.Exception;
                    if (!completed.IsFaulted && !completed.IsCanceled) return;

                    lock (_gate)
                    {
                        if (ReferenceEquals(_loadTask, task)) _loadTask = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
