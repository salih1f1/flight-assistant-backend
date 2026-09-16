using FlightAssistant.API.HostedServices;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// <see cref="VerificationJobHeartbeat"/>, the state the admin health report reads for
/// <see cref="ReferenceVerificationBackgroundService"/>. Tested directly: the integration factory
/// removes every hosted service, so the job never runs in an integration test.
/// </summary>
public class VerificationJobHeartbeatTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(T0));

    [Fact]
    public void Current_InitialState_IsNeverRunWithNullTimestampsAndZeroCounts()
    {
        var snapshot = new VerificationJobHeartbeat(_time).Current;

        Assert.Equal(
            new VerificationJobSnapshot(null, null, VerificationJobOutcome.NeverRun, 0, 0, 0, 0, null, null),
            snapshot);
    }

    [Fact]
    public void MarkRunStarted_SetsRunningAtNow_AndKeepsThePreviousCyclesCountsAndCompletionTime()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunSucceeded(processed: 8, failed: 2, stuckAbandoned: 1, failuresUnrecorded: 1, nextRunDueUtc: T0.AddSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(30));
        heartbeat.MarkRunStarted();
        var snapshot = heartbeat.Current;

        Assert.Equal(VerificationJobOutcome.Running, snapshot.LastOutcome);
        Assert.Equal(T0.AddSeconds(30), snapshot.LastRunStartedUtc);
        Assert.Equal(T0, snapshot.LastRunCompletedUtc);
        Assert.Equal(8, snapshot.RequestsProcessed);
        Assert.Equal(2, snapshot.RequestsFailed);
        Assert.Equal(1, snapshot.StuckAbandoned);
        Assert.Equal(1, snapshot.FailuresUnrecorded);
    }

    /// <summary>A successful cycle records both new counts, exactly as it does
    /// <c>RequestsProcessed</c>/<c>RequestsFailed</c> (R5).</summary>
    [Fact]
    public void MarkRunSucceeded_WithSomeRequestsFailed_IsStillSuccess_WithCountsNextDueAndNoError()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunFailed(new InvalidOperationException("earlier"), T0.AddSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(5));
        heartbeat.MarkRunSucceeded(processed: 10, failed: 3, stuckAbandoned: 2, failuresUnrecorded: 1, nextRunDueUtc: T0.AddSeconds(35));

        Assert.Equal(
            new VerificationJobSnapshot(T0, T0.AddSeconds(5), VerificationJobOutcome.Success, 10, 3, 2, 1, T0.AddSeconds(35), null),
            heartbeat.Current);
    }

    [Fact]
    public void MarkRunFailed_RecordsTypeAndMessageOnly_AndTheNextDueTime()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunStarted();
        Exception ex;
        try
        {
            throw new TimeoutException("claim timed out");
        }
        catch (TimeoutException caught)
        {
            ex = caught; // a real stack trace, which must not be recorded
        }

        heartbeat.MarkRunFailed(ex, T0.AddSeconds(30));
        var snapshot = heartbeat.Current;

        Assert.Equal(VerificationJobOutcome.Failed, snapshot.LastOutcome);
        Assert.Equal("TimeoutException: claim timed out", snapshot.LastError);
        Assert.Equal(T0.AddSeconds(30), snapshot.NextRunDueUtc);
        Assert.Equal(T0, snapshot.LastRunCompletedUtc);
    }

    /// <summary>The plain overload — a cycle that failed before it finished its batch — leaves every
    /// one of the previous cycle's counts in place, new fields included (R5).</summary>
    [Fact]
    public void MarkRunFailed_BeforeTheBatchFinished_KeepsThePreviousCyclesCounts()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunSucceeded(processed: 9, failed: 4, stuckAbandoned: 3, failuresUnrecorded: 2, nextRunDueUtc: T0.AddSeconds(30));

        heartbeat.MarkRunFailed(new TimeoutException("claim timed out"), T0.AddSeconds(60));

        var snapshot = heartbeat.Current;
        Assert.Equal((9, 4, 3, 2), (snapshot.RequestsProcessed, snapshot.RequestsFailed, snapshot.StuckAbandoned, snapshot.FailuresUnrecorded));
    }

    /// <summary>The batch-aware overload replaces every count, new fields included, with the failed
    /// cycle's own — never the previous cycle's (R5).</summary>
    [Fact]
    public void MarkRunFailed_AfterTheBatchWasProcessed_ReplacesTheCountsWithThatBatchs()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunSucceeded(processed: 9, failed: 4, stuckAbandoned: 3, failuresUnrecorded: 2, nextRunDueUtc: T0.AddSeconds(30));

        heartbeat.MarkRunFailed(
            new InvalidOperationException("release boom"), T0.AddSeconds(60),
            processed: 5, failed: 2, stuckAbandoned: 1, failuresUnrecorded: 0);

        Assert.Equal(
            new VerificationJobSnapshot(null, T0, VerificationJobOutcome.Failed, 5, 2, 1, 0, T0.AddSeconds(60), "InvalidOperationException: release boom"),
            heartbeat.Current);
    }

    // ── AeroAPI budget pause (R4/R5, #401 task 2) ───────────────────────────

    /// <summary>A paused cycle gets its own outcome, distinct from both Success and Failed, so an
    /// operator can tell "the queue isn't draining because of budget" apart from either at a glance.</summary>
    [Fact]
    public void MarkRunPaused_SetsThePausedOutcome_WithEveryCountIncludingRequestsPaused()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunStarted();

        heartbeat.MarkRunPaused(processed: 6, failed: 1, stuckAbandoned: 0, failuresUnrecorded: 0, paused: 4, nextRunDueUtc: T0.AddSeconds(30));

        Assert.Equal(
            new VerificationJobSnapshot(T0, T0, VerificationJobOutcome.Paused, 6, 1, 0, 0, T0.AddSeconds(30), null, RequestsPaused: 4),
            heartbeat.Current);
    }

    /// <summary>A subsequent ordinary success clears a stale RequestsPaused left by an earlier paused
    /// cycle -- otherwise the count would keep reporting a pause that already resolved.</summary>
    [Fact]
    public void MarkRunSucceeded_AfterAPausedCycle_ClearsRequestsPaused()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunPaused(processed: 6, failed: 1, stuckAbandoned: 0, failuresUnrecorded: 0, paused: 4, nextRunDueUtc: T0.AddSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(30));
        heartbeat.MarkRunSucceeded(processed: 3, failed: 0, stuckAbandoned: 0, failuresUnrecorded: 0, nextRunDueUtc: T0.AddSeconds(60));

        var snapshot = heartbeat.Current;
        Assert.Equal(VerificationJobOutcome.Success, snapshot.LastOutcome);
        Assert.Equal(0, snapshot.RequestsPaused);
    }

    /// <summary>The batch-aware failed overload also carries a paused count: a cycle can pause some
    /// requests and then separately fail releasing source caches, and both facts belong on the report.</summary>
    [Fact]
    public void MarkRunFailed_AfterTheBatchWasProcessed_CarriesThatBatchsPausedCountToo()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);

        heartbeat.MarkRunFailed(
            new InvalidOperationException("release boom"), T0.AddSeconds(60),
            processed: 5, failed: 2, stuckAbandoned: 1, failuresUnrecorded: 0, paused: 3);

        Assert.Equal(3, heartbeat.Current.RequestsPaused);
        Assert.Equal(VerificationJobOutcome.Failed, heartbeat.Current.LastOutcome);
    }
}
