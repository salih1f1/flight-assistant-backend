using FlightAssistant.API.HostedServices;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// <see cref="FlightSyncHeartbeat"/> is the observable state the admin health endpoint (#311 task 2)
/// will read for <see cref="FlightStatusSyncBackgroundService"/>. These tests exercise it directly,
/// since the integration test factory removes every <c>IHostedService</c> and the background
/// service never actually runs in an integration test.
/// </summary>
public class FlightSyncHeartbeatTests
{
    [Fact]
    public void Current_InitialState_IsNeverRunWithNullTimestampsAndZeroCounts()
    {
        var heartbeat = new FlightSyncHeartbeat();

        var snapshot = heartbeat.Current;

        Assert.Equal(FlightSyncOutcome.NeverRun, snapshot.LastOutcome);
        Assert.Null(snapshot.LastRunStartedUtc);
        Assert.Null(snapshot.LastRunCompletedUtc);
        Assert.Null(snapshot.NextRunDueUtc);
        Assert.Equal(0, snapshot.FlightsProcessed);
        Assert.Equal(0, snapshot.FlightsFailed);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public void MarkRunStarted_SetsRunning_AndPreservesThePriorRunsCountsAndCompletionTime()
    {
        var heartbeat = new FlightSyncHeartbeat();
        var nextDue = DateTime.UtcNow.AddMinutes(10);
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunSucceeded(processed: 12, failed: 3, nextRunDueUtc: nextDue);
        var priorCompletedUtc = heartbeat.Current.LastRunCompletedUtc;

        heartbeat.MarkRunStarted();
        var snapshot = heartbeat.Current;

        Assert.Equal(FlightSyncOutcome.Running, snapshot.LastOutcome);
        Assert.NotNull(snapshot.LastRunStartedUtc);
        // The previous run's completion timestamp and counts are untouched by a new start.
        Assert.Equal(priorCompletedUtc, snapshot.LastRunCompletedUtc);
        Assert.Equal(12, snapshot.FlightsProcessed);
        Assert.Equal(3, snapshot.FlightsFailed);
    }

    [Fact]
    public void MarkRunSucceeded_SetsCountsAndNextDue_AndClearsAPreviouslySetError()
    {
        var heartbeat = new FlightSyncHeartbeat();
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunFailed(new InvalidOperationException("boom"), DateTime.UtcNow.AddMinutes(10));
        Assert.NotNull(heartbeat.Current.LastError);

        var nextDue = DateTime.UtcNow.AddMinutes(10);
        heartbeat.MarkRunSucceeded(processed: 5, failed: 0, nextRunDueUtc: nextDue);
        var snapshot = heartbeat.Current;

        Assert.Equal(FlightSyncOutcome.Success, snapshot.LastOutcome);
        Assert.Equal(5, snapshot.FlightsProcessed);
        Assert.Equal(0, snapshot.FlightsFailed);
        Assert.Equal(nextDue, snapshot.NextRunDueUtc);
        Assert.NotNull(snapshot.LastRunCompletedUtc);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public void MarkRunSucceeded_WithSomeFlightsFailed_StillReportsOutcomeSuccess()
    {
        // This is the distinction the whole feature turns on: a batch that ran to completion is a
        // healthy sync loop even when individual flights inside it threw. FlightsFailed — not
        // LastOutcome — is what tells the operator something is wrong.
        var heartbeat = new FlightSyncHeartbeat();
        heartbeat.MarkRunStarted();

        heartbeat.MarkRunSucceeded(processed: 20, failed: 7, nextRunDueUtc: DateTime.UtcNow.AddMinutes(10));
        var snapshot = heartbeat.Current;

        Assert.Equal(FlightSyncOutcome.Success, snapshot.LastOutcome);
        Assert.Equal(20, snapshot.FlightsProcessed);
        Assert.Equal(7, snapshot.FlightsFailed);
    }

    [Fact]
    public void MarkRunFailed_RecordsTypeAndMessage_AndNeverAStackTrace()
    {
        var heartbeat = new FlightSyncHeartbeat();
        heartbeat.MarkRunStarted();

        const string message = "database connection refused";
        Exception ex;
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException caught)
        {
            ex = caught; // ensure a real stack trace is populated on the exception
        }

        var nextDue = DateTime.UtcNow.AddMinutes(10);
        heartbeat.MarkRunFailed(ex, nextDue);
        var snapshot = heartbeat.Current;

        Assert.Equal(FlightSyncOutcome.Failed, snapshot.LastOutcome);
        // Exact-string equality (rather than asserting the absence of e.g. "at FlightAssistant" or
        // Environment.NewLine) is the strong form of "never a stack trace" for a value that's rendered
        // to an operator outside Production: it pins the whole value, so nothing beyond the type name
        // and message — stack trace included — can ever sneak in.
        Assert.Equal($"{typeof(InvalidOperationException).Name}: {message}", snapshot.LastError);
        Assert.Equal(nextDue, snapshot.NextRunDueUtc);
        Assert.NotNull(snapshot.LastRunCompletedUtc);
    }

    [Fact]
    public async Task Current_NeverExposesFieldsMixedFromDifferentWriters_RegressionGuardForImmutableSnapshotDesign()
    {
        // What this guards: FlightSyncSnapshot is an immutable record published via a single atomic
        // reference write, so .NET already guarantees a reader can never observe a torn *instance* —
        // that holds with or without the `lock`. The real risk this test catches is a future
        // regression to individual mutable fields on the heartbeat (separate LastOutcome /
        // FlightsProcessed / FlightsFailed fields updated one at a time): that shape would let a
        // concurrent reader observe a snapshot stitched together from two different writers, e.g. a
        // fresh Success outcome paired with the wrong writer's counts. Several writer threads publish
        // distinguishable (processed, failed) pairs below so such a mixture is detectable rather than
        // accidentally coherent.
        //
        // What this does NOT show: that the `lock` inside the Mark* methods is load-bearing today.
        // With a single production writer (the background service's sequential loop) there is nothing
        // yet for it to race against; it stays as insurance against a second writer being added later.
        const int writerCount = 4;
        const int iterationsPerWriter = 2000;
        var heartbeat = new FlightSyncHeartbeat();
        using var cts = new CancellationTokenSource();
        var incoherentSnapshotObserved = false;

        // Writer `w` always publishes the pair (w, -(w + 1)) together in one call, so the pairing
        // itself — not just the individual values — identifies which writer's write produced it. A
        // snapshot mixing fields from two different writes can never satisfy failed == -(processed+1).
        var writers = Enumerable.Range(0, writerCount)
            .Select(writerId => Task.Run(() =>
            {
                for (var i = 0; i < iterationsPerWriter; i++)
                {
                    heartbeat.MarkRunStarted();
                    heartbeat.MarkRunSucceeded(
                        processed: writerId,
                        failed: -(writerId + 1),
                        nextRunDueUtc: DateTime.UtcNow.AddMinutes(10));
                }
            }))
            .ToArray();

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = heartbeat.Current;

                // A completed outcome must always carry its completion timestamp — never a fresh
                // outcome sitting beside a still-null timestamp from before the run started.
                var completedOutcomeHasTimestamp =
                    snapshot.LastOutcome is not (FlightSyncOutcome.Success or FlightSyncOutcome.Failed)
                    || snapshot.LastRunCompletedUtc is not null;

                // A Success snapshot's counts must be the exact pair one writer published together.
                var countsAreASingleWritersPair =
                    snapshot.LastOutcome != FlightSyncOutcome.Success
                    || snapshot.FlightsFailed == -(snapshot.FlightsProcessed + 1);

                if (!completedOutcomeHasTimestamp || !countsAreASingleWritersPair)
                    incoherentSnapshotObserved = true;
            }
        });

        await Task.WhenAll(writers);
        cts.Cancel();
        await reader;

        Assert.False(incoherentSnapshotObserved);
    }

    [Fact]
    public void IsActiveForFastPoll_StillWorks_UnaffectedByHeartbeatChanges()
    {
        // Guard against accidentally touching the untouched static predicate while wiring the
        // heartbeat through FlightStatusSyncBackgroundService.
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var result = FlightStatusSyncBackgroundService.IsActiveForFastPoll(
            scheduledDepartureUtc: now.AddHours(2),
            scheduledArrivalUtc: now.AddHours(4),
            actualArrivalUtc: null,
            now: now);

        Assert.True(result);
    }
}
