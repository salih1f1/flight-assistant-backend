using FlightAssistant.Application.Verification;

namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>Settings shared by every <see cref="VerificationDatasetCache{T}"/>.</summary>
    internal static class VerificationDatasetCache
    {
        /// <summary>
        /// How long a failed load's error is handed to later lookups before one of them downloads
        /// again. It keeps a batch from repeating a failing download once per request: a stalled
        /// upstream would otherwise cost the full download deadline per request, and a reshaped one
        /// a full download and parse per request. No longer than
        /// <see cref="VerificationRetryBackoff.AfterFirstAttempt"/>, so a request's first retry never
        /// lands inside the cooldown of the failure that scheduled it.
        /// </summary>
        public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);
    }

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
    /// <b>Failures (final fix wave, A).</b> A load that faults is kept for the failure cooldown
    /// (<see cref="VerificationDatasetCache.FailureCooldown"/>, counted from when it failed): every
    /// lookup inside it gets the same error straight away, with no new download, and the first
    /// lookup after it downloads again. <see cref="Release"/> drops a cached failure like a cached
    /// dataset. A load that ends <b>cancelled</b> is not a source failure (the load runs on this
    /// cache's own token, so no caller cancelled it) and is dropped at once, never replayed.
    /// </para>
    /// </summary>
    internal sealed class VerificationDatasetCache<T> where T : class
    {
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _maxAge;
        private readonly TimeSpan _failureCooldown;
        private readonly object _gate = new();
        private Task<T>? _loadTask;
        private DateTimeOffset _loadedAtUtc;

        /// <summary>When <see cref="_loadTask"/> faulted; null while it has not, and until the
        /// continuation that records the failure has run.</summary>
        private DateTimeOffset? _failedAtUtc;

        /// <param name="failureCooldown">Defaults to <see cref="VerificationDatasetCache.FailureCooldown"/>;
        /// at most <see cref="VerificationRetryBackoff.AfterFirstAttempt"/>.</param>
        public VerificationDatasetCache(TimeProvider timeProvider, TimeSpan maxAge, TimeSpan? failureCooldown = null)
        {
            _timeProvider = timeProvider;
            _maxAge = maxAge;
            _failureCooldown = failureCooldown ?? VerificationDatasetCache.FailureCooldown;
            ArgumentOutOfRangeException.ThrowIfLessThan(_failureCooldown, TimeSpan.Zero, nameof(failureCooldown));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                _failureCooldown, VerificationRetryBackoff.AfterFirstAttempt, nameof(failureCooldown));
        }

        /// <summary>
        /// Returns the cached dataset, calling <paramref name="load"/> to (re)download and parse it
        /// only when nothing is cached, the cached copy is at least <c>maxAge</c> old, or a prior
        /// load failed more than the failure cooldown ago. Inside that cooldown the prior load's
        /// error is thrown again without calling <paramref name="load"/>. <paramref name="load"/> is
        /// always invoked with <see cref="CancellationToken.None"/> -- see the "Per-caller
        /// cancellation" remark on this type -- while <paramref name="ct"/> is used only to bound
        /// <b>this</b> call's own wait for the (possibly shared) result.
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
                if (_loadTask is { } current && CanReuse(current, now))
                {
                    task = current;
                }
                else
                {
                    _loadedAtUtc = now;
                    _failedAtUtc = null;
                    // Captured into the local `task` *before* attaching the bookkeeping continuation:
                    // when `load` fails synchronously (a pre-faulted Task, common with a fake
                    // HttpMessageHandler in tests), ExecuteSynchronously below runs that continuation
                    // immediately, right here inside this (re-entrant) lock -- and for a cancelled
                    // load it nulls `_loadTask`, so re-reading the field afterwards could hand this
                    // caller a null Task instead of the very one it just started.
                    task = load(CancellationToken.None);
                    _loadTask = task;
                    AttachFailureBookkeeping(task);
                }
            }

            return task.WaitAsync(ct);
        }

        /// <summary>Whether anything is held for <see cref="Release"/> to drop: a loaded dataset (even
        /// one past its maximum age), a load in flight, or a failed load. No I/O.</summary>
        public bool HasCachedData
        {
            get
            {
                lock (_gate)
                {
                    return _loadTask is not null;
                }
            }
        }

        /// <summary>Drops the cached dataset or cached failure, if any, so the next
        /// <see cref="GetAsync"/> call downloads again. Safe to call when nothing is cached.</summary>
        public void Release()
        {
            lock (_gate)
            {
                _loadTask = null;
                _failedAtUtc = null;
            }
        }

        /// <summary>Whether <paramref name="current"/> can answer a call made at
        /// <paramref name="now"/>. Called under <see cref="_gate"/>.</summary>
        private bool CanReuse(Task<T> current, DateTimeOffset now)
        {
            if (current.IsFaulted)
            {
                // Until the continuation has recorded the failure, the load has only just failed.
                return now - (_failedAtUtc ?? now) < _failureCooldown;
            }

            if (current.IsCanceled)
                return false;

            // In flight, or loaded successfully.
            return now - _loadedAtUtc < _maxAge;
        }

        /// <summary>
        /// Fires once, independently of how many callers are awaiting <paramref name="task"/> or how
        /// many of them cancel their own wait. A faulted <paramref name="task"/> stays cached with its
        /// failure time, which starts the failure cooldown; a cancelled one is evicted at once. Either
        /// way only while <paramref name="task"/> is still the current entry -- a newer load (after a
        /// <see cref="Release"/>, or after a cooldown) must not be clobbered by a straggling failure
        /// from an older, abandoned load.
        /// </summary>
        private void AttachFailureBookkeeping(Task<T> task)
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
                        if (!ReferenceEquals(_loadTask, task)) return;

                        if (completed.IsFaulted)
                            _failedAtUtc = _timeProvider.GetUtcNow();
                        else
                            _loadTask = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
