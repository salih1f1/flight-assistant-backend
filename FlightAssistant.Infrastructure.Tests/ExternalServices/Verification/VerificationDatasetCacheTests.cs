using FlightAssistant.Application.Verification;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// Unit tests for <see cref="VerificationDatasetCache{T}"/> in isolation from either source --
/// the shared download-once/reuse-24h/release logic <c>OurAirportsVerificationSource</c> and
/// <c>MledozeCountriesVerificationSource</c> both delegate to. Internal type, visible here via the
/// existing <c>InternalsVisibleTo("FlightAssistant.Infrastructure.Tests")</c>.
/// </summary>
public class VerificationDatasetCacheTests
{
    private static VerificationDatasetCache<string> NewCache(TimeProvider timeProvider) =>
        new(timeProvider, TimeSpan.FromHours(24));

    /// <summary>The production default of <c>Verification:IdleRelease</c> (#378).</summary>
    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task GetAsync_SecondCallWithinMaxAge_DoesNotReload()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        var first = await cache.GetAsync(Load, default);
        clock.Advance(TimeSpan.FromHours(23));
        var second = await cache.GetAsync(Load, default);

        Assert.Equal(1, loadCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetAsync_AfterMaxAge_Reloads()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        await cache.GetAsync(Load, default);
        clock.Advance(TimeSpan.FromHours(24));
        var second = await cache.GetAsync(Load, default);

        Assert.Equal(2, loadCount);
        Assert.Equal("v2", second);
    }

    [Fact]
    public async Task Release_MakesTheNextGetAsyncReload()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        await cache.GetAsync(Load, default);
        cache.Release(TimeSpan.Zero);
        await cache.GetAsync(Load, default);

        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void GetAsync_WithAnAlreadyCancelledToken_ThrowsWithoutCallingLoad()
    {
        // Fix round 2, item 4: an already-given-up caller must never be the one that starts a
        // brand new download for everyone else.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCalled = false;
        Task<string> Load(CancellationToken ct) { loadCalled = true; return Task.FromResult("v"); }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Not Assert.Throws/ThrowsAsync: GetAsync returns Task<T>, so xUnit's analyzer insists on
        // ThrowsAsync -- but the throw here is genuinely synchronous (GetAsync itself is not
        // async), which ThrowsAsync cannot assert (it awaits the returned Task, which never gets
        // the chance to exist). A plain try/catch proves the synchronous-throw contract directly.
        var threw = false;
        try
        {
            cache.GetAsync(Load, cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Assert.True(threw);
        Assert.False(loadCalled);
    }

    [Fact]
    public async Task GetAsync_TwoCallsThatBothMissTheCache_ShareOneLoad()
    {
        // Deterministic (fix round 1, item 10c): waits for proof `load` was actually entered,
        // rather than an arbitrary sleep, before issuing the second call and releasing the gate.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref loadCount);
            entered.TrySetResult();
            await release.Task;
            return "value";
        }

        var first = cache.GetAsync(Load, default);
        await entered.Task;
        var second = cache.GetAsync(Load, default);

        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, loadCount);
        Assert.Equal("value", results[0]);
        Assert.Equal("value", results[1]);
    }

    [Fact]
    public async Task GetAsync_OneCallersCancellation_DoesNotAbortTheSharedLoadForAnother()
    {
        // Fix round 1, item 7: `load` runs on a token this cache owns, not any individual caller's
        // -- cancelling the first caller's own `ct` must not tear down the shared download that a
        // second, still-interested caller is awaiting.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Load(CancellationToken ct)
        {
            entered.TrySetResult();
            // Fix round 2, item 6: honours `ct` so this test can actually detect a regression to
            // `load(ct)` (the caller's own token) instead of the cache-owned CancellationToken.None.
            // In the correct implementation `ct` here is always None, so this never cancels; if the
            // cache regressed to passing the first caller's real token, cancelling it below would
            // tear down this shared load out from under the still-waiting second caller too.
            await release.Task.WaitAsync(ct);
            return "value";
        }

        using var firstCts = new CancellationTokenSource();
        var first = cache.GetAsync(Load, firstCts.Token);
        await entered.Task;
        var second = cache.GetAsync(Load, default);

        firstCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        release.SetResult();
        Assert.Equal("value", await second);
    }

    // A deterministic test for "the eviction continuation observes a fault so
    // TaskScheduler.UnobservedTaskException never fires" was attempted here (cancel the only
    // caller, fault the load, force GC.Collect()+WaitForPendingFinalizers() twice, assert the event
    // did not fire) and found NOT reliably distinguishing in this test host: the event did not fire
    // even with the `_ = completed.Exception;` fix in VerificationDatasetCache temporarily removed
    // (tried 3x). So no test is committed for this. The one-line production fix itself is kept
    // regardless -- it is cheap, correct by inspection, and does not depend on the test working to
    // be worth having.

    [Fact]
    public async Task HasCachedData_TracksALoadedDataset_ACachedFailure_AndRelease()
    {
        var cache = NewCache(new FakeTimeProvider(DateTimeOffset.UtcNow));
        Assert.False(cache.HasCachedData);

        await cache.GetAsync(_ => Task.FromResult("value"), default);
        Assert.True(cache.HasCachedData);
        cache.Release(TimeSpan.Zero);
        Assert.False(cache.HasCachedData);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetAsync(_ => Task.FromException<string>(new InvalidOperationException("boom")), default));
        Assert.True(cache.HasCachedData); // the failure is held for its cooldown
        cache.Release(TimeSpan.Zero);
        Assert.False(cache.HasCachedData);
    }

    // ── Failure cooldown (final fix wave, A) ─────────────────────────────────

    [Fact]
    public async Task GetAsync_WithinTheFailureCooldown_ThrowsTheSameFailure_WithoutLoadingAgain()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) => ++loadCount == 1
            ? Task.FromException<string>(new InvalidOperationException("boom"))
            : Task.FromResult("recovered");

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Load, default));
        clock.Advance(VerificationDatasetCache.FailureCooldown - TimeSpan.FromTicks(1));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Load, default));

        Assert.Equal(1, loadCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetAsync_OnceTheFailureCooldownHasPassed_LoadsAgain()
    {
        // The failure arrives after GetAsync returned, so the continuation (not the synchronous
        // pre-faulted path) records when it failed: the cooldown counts from then, not from the load's start.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var failing = new TaskCompletionSource<string>();
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) => ++loadCount == 1 ? failing.Task : Task.FromResult("recovered");

        var first = cache.GetAsync(Load, default);
        clock.Advance(TimeSpan.FromSeconds(30));
        failing.SetException(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);

        clock.Advance(VerificationDatasetCache.FailureCooldown - TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Load, default));
        Assert.Equal(1, loadCount);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("recovered", await cache.GetAsync(Load, default));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task Release_DuringTheFailureCooldown_LetsTheNextCallLoadAgain()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) => ++loadCount == 1
            ? Task.FromException<string>(new InvalidOperationException("boom"))
            : Task.FromResult("recovered");

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Load, default));
        cache.Release(TimeSpan.Zero);

        Assert.Equal("recovered", await cache.GetAsync(Load, default));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task GetAsync_ALoadThatEndsCancelled_IsNotCachedAsAFailure_TheNextCallLoadsAgain()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) => ++loadCount == 1
            ? Task.FromCanceled<string>(new CancellationToken(canceled: true))
            : Task.FromResult("recovered");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(Load, default));

        Assert.Equal("recovered", await cache.GetAsync(Load, default));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task GetAsync_TheOnlyCallerCancellingItsWait_CachesNoFailure_AndTheLoadStillServesTheNextCall()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loading = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return loading.Task; }

        using var cts = new CancellationTokenSource();
        var waiting = cache.GetAsync(Load, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        loading.SetResult("value");
        Assert.Equal("value", await cache.GetAsync(Load, default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task GetAsync_AnOlderLoadFailingAfterANewerOneSucceeded_DoesNotCacheItsFailure()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var older = new TaskCompletionSource<string>();
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) => ++loadCount == 1 ? older.Task : Task.FromResult("newer");

        var olderWait = cache.GetAsync(Load, default);
        cache.Release(TimeSpan.Zero);
        Assert.Equal("newer", await cache.GetAsync(Load, default));

        older.SetException(new InvalidOperationException("late"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => olderWait);

        Assert.Equal("newer", await cache.GetAsync(Load, default));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void FailureCooldown_IsNoLongerThanTheFirstRetryBackoff_AndALongerOneIsRejected()
    {
        Assert.True(VerificationDatasetCache.FailureCooldown <= VerificationRetryBackoff.AfterFirstAttempt);

        Assert.Throws<ArgumentOutOfRangeException>(() => new VerificationDatasetCache<string>(
            TimeProvider.System, TimeSpan.FromHours(24), VerificationRetryBackoff.AfterFirstAttempt + TimeSpan.FromTicks(1)));
    }

    // ── Idle release (#378) ──────────────────────────────────────────────────

    [Fact]
    public async Task Release_ADatasetUsedLessThanTheIdlePeriodAgo_IsKept()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        await cache.GetAsync(Load, default);
        clock.Advance(TimeSpan.FromMinutes(20));
        await cache.GetAsync(Load, default); // a later lookup restarts the idle period
        clock.Advance(IdleRelease - TimeSpan.FromTicks(1));
        cache.Release(IdleRelease);

        Assert.True(cache.HasCachedData);
        Assert.Equal("v1", await cache.GetAsync(Load, default));
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task Release_ADatasetIdleForLongerThanTheIdlePeriod_IsDropped_AndTheNextCallLoadsAgain()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        await cache.GetAsync(Load, default);
        clock.Advance(IdleRelease + TimeSpan.FromMinutes(1));
        cache.Release(IdleRelease);

        Assert.False(cache.HasCachedData);
        Assert.Equal("v2", await cache.GetAsync(Load, default));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task Release_ADatasetIdleForExactlyTheIdlePeriod_IsDropped()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);

        await cache.GetAsync(_ => Task.FromResult("value"), default);
        clock.Advance(IdleRelease);
        cache.Release(IdleRelease);

        Assert.False(cache.HasCachedData);
    }

    [Fact]
    public async Task Release_WithAZeroIdlePeriod_DropsADatasetUsedThisInstant_AndALoadInFlight()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);

        await cache.GetAsync(_ => Task.FromResult("value"), default);
        cache.Release(TimeSpan.Zero);
        Assert.False(cache.HasCachedData);

        var loading = new TaskCompletionSource<string>();
        var waiting = cache.GetAsync(_ => loading.Task, default);
        cache.Release(TimeSpan.Zero);
        Assert.False(cache.HasCachedData);

        // The caller already waiting still gets its dataset; it just isn't kept.
        loading.SetResult("late");
        Assert.Equal("late", await waiting);
        Assert.False(cache.HasCachedData);
    }

    [Fact]
    public async Task Release_WithAZeroIdlePeriod_DropsADataset_EvenAfterTheClockSteppedBackSinceItsLastUse()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);

        await cache.GetAsync(_ => Task.FromResult("value"), default);
        clock.AdjustTime(clock.GetUtcNow() - TimeSpan.FromSeconds(1)); // a wall clock corrected backwards
        cache.Release(TimeSpan.Zero);

        Assert.False(cache.HasCachedData);
    }

    [Fact]
    public void Release_WithANegativeIdlePeriod_Throws()
    {
        var cache = NewCache(new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Release(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public async Task Release_ALoadInFlight_IsKept_HoweverLongItHasRun()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loading = new TaskCompletionSource<string>();
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return loading.Task; }

        var waiting = cache.GetAsync(Load, default);
        clock.Advance(IdleRelease + TimeSpan.FromMinutes(1));
        cache.Release(IdleRelease);

        Assert.True(cache.HasCachedData);
        loading.SetResult("value");
        Assert.Equal("value", await waiting);
        Assert.Equal("value", await cache.GetAsync(Load, default));
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task Release_ALoadedDataset_IsIdleFromWhenItsLoadFinished_NotFromWhenItStarted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loading = new TaskCompletionSource<string>();

        var waiting = cache.GetAsync(_ => loading.Task, default);
        clock.Advance(TimeSpan.FromMinutes(20));
        loading.SetResult("value"); // answers the waiting call now: its last use
        Assert.Equal("value", await waiting);

        clock.Advance(IdleRelease - TimeSpan.FromTicks(1));
        cache.Release(IdleRelease);
        Assert.True(cache.HasCachedData);

        clock.Advance(TimeSpan.FromTicks(1));
        cache.Release(IdleRelease);
        Assert.False(cache.HasCachedData);
    }

    /// <summary>A cached failure is not kept for being recent, even right after a lookup inside its
    /// cooldown: release drops it, and the next call loads again. (Its cooldown expiring on its own is
    /// <see cref="GetAsync_OnceTheFailureCooldownHasPassed_LoadsAgain"/>.)</summary>
    [Fact]
    public async Task Release_ACachedFailure_IsDroppedAtOnce_NotKeptForTheIdlePeriod()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) => ++loadCount == 1
            ? Task.FromException<string>(new InvalidOperationException("boom"))
            : Task.FromResult("recovered");

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Load, default));
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Load, default));
        cache.Release(IdleRelease);

        Assert.False(cache.HasCachedData);
        Assert.Equal("recovered", await cache.GetAsync(Load, default));
        Assert.Equal(2, loadCount);
    }

    /// <summary>A dataset at its maximum age is never answered from again (the next call downloads), so a
    /// release drops it however recently it was used instead of holding it for the idle period.</summary>
    [Fact]
    public async Task Release_ADatasetAtItsMaximumAge_IsDropped_EvenThoughItWasUsedWithinTheIdlePeriod()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        await cache.GetAsync(Load, default);
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));
        Assert.Equal("v1", await cache.GetAsync(Load, default)); // reused a minute before the cap
        clock.Advance(TimeSpan.FromSeconds(30));
        cache.Release(IdleRelease);
        Assert.True(cache.HasCachedData); // within its maximum age, used 30 s ago: kept

        clock.Advance(TimeSpan.FromSeconds(30)); // exactly 24 h after the download, used a minute ago
        cache.Release(IdleRelease);

        Assert.False(cache.HasCachedData);
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task GetAsync_ADatasetUsedTooOftenToGoIdle_IsStillDownloadedAgainAtItsMaximumAge()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        await cache.GetAsync(Load, default);
        for (var i = 1; i < 144; i++) // a lookup and a release every 10 minutes, for 23h50m
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal("v1", await cache.GetAsync(Load, default));
            cache.Release(IdleRelease);
            Assert.True(cache.HasCachedData);
        }

        clock.Advance(TimeSpan.FromMinutes(10)); // 24 hours after the download
        Assert.Equal("v2", await cache.GetAsync(Load, default));
        Assert.Equal(2, loadCount);
    }
}
