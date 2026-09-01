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

        Exception ex;
        try
        {
            throw new InvalidOperationException("database connection refused");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught; // ensure a real stack trace is populated on the exception
        }

        var nextDue = DateTime.UtcNow.AddMinutes(10);
        heartbeat.MarkRunFailed(ex, nextDue);
        var snapshot = heartbeat.Current;

        Assert.Equal(FlightSyncOutcome.Failed, snapshot.LastOutcome);
        Assert.Equal("InvalidOperationException: database connection refused", snapshot.LastError);
        Assert.DoesNotContain("at FlightAssistant", snapshot.LastError);
        Assert.DoesNotContain(Environment.NewLine, snapshot.LastError);
        Assert.Equal(nextDue, snapshot.NextRunDueUtc);
        Assert.NotNull(snapshot.LastRunCompletedUtc);
    }

    [Fact]
    public async Task Current_NeverReturnsATornSnapshot_UnderConcurrentReadsAndWrites()
    {
        var heartbeat = new FlightSyncHeartbeat();
        using var cts = new CancellationTokenSource();
        var observedTornSnapshot = false;

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++)
            {
                heartbeat.MarkRunStarted();
                if (i % 2 == 0)
                    heartbeat.MarkRunSucceeded(i, i % 3, DateTime.UtcNow.AddMinutes(10));
                else
                    heartbeat.MarkRunFailed(new InvalidOperationException("boom"), DateTime.UtcNow.AddMinutes(10));
            }

            cts.Cancel();
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = heartbeat.Current;

                // A completed outcome (Success/Failed) must always carry its completion timestamp —
                // never one field from a new write sitting beside another from the previous one.
                var isTorn = snapshot.LastOutcome is FlightSyncOutcome.Success or FlightSyncOutcome.Failed
                    && snapshot.LastRunCompletedUtc is null;

                if (isTorn)
                    observedTornSnapshot = true;
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.False(observedTornSnapshot);
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
