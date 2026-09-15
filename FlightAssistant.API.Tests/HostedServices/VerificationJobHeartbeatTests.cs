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
            new VerificationJobSnapshot(null, null, VerificationJobOutcome.NeverRun, 0, 0, null, null),
            snapshot);
    }

    [Fact]
    public void MarkRunStarted_SetsRunningAtNow_AndKeepsThePreviousCyclesCountsAndCompletionTime()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunSucceeded(processed: 8, failed: 2, nextRunDueUtc: T0.AddSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(30));
        heartbeat.MarkRunStarted();
        var snapshot = heartbeat.Current;

        Assert.Equal(VerificationJobOutcome.Running, snapshot.LastOutcome);
        Assert.Equal(T0.AddSeconds(30), snapshot.LastRunStartedUtc);
        Assert.Equal(T0, snapshot.LastRunCompletedUtc);
        Assert.Equal(8, snapshot.RequestsProcessed);
        Assert.Equal(2, snapshot.RequestsFailed);
    }

    [Fact]
    public void MarkRunSucceeded_WithSomeRequestsFailed_IsStillSuccess_WithCountsNextDueAndNoError()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunFailed(new InvalidOperationException("earlier"), T0.AddSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(5));
        heartbeat.MarkRunSucceeded(processed: 10, failed: 3, nextRunDueUtc: T0.AddSeconds(35));

        Assert.Equal(
            new VerificationJobSnapshot(T0, T0.AddSeconds(5), VerificationJobOutcome.Success, 10, 3, T0.AddSeconds(35), null),
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

    [Fact]
    public void MarkRunFailed_BeforeTheBatchFinished_KeepsThePreviousCyclesCounts()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunSucceeded(processed: 9, failed: 4, nextRunDueUtc: T0.AddSeconds(30));

        heartbeat.MarkRunFailed(new TimeoutException("claim timed out"), T0.AddSeconds(60));

        Assert.Equal((9, 4), (heartbeat.Current.RequestsProcessed, heartbeat.Current.RequestsFailed));
    }

    [Fact]
    public void MarkRunFailed_AfterTheBatchWasProcessed_ReplacesTheCountsWithThatBatchs()
    {
        var heartbeat = new VerificationJobHeartbeat(_time);
        heartbeat.MarkRunSucceeded(processed: 9, failed: 4, nextRunDueUtc: T0.AddSeconds(30));

        heartbeat.MarkRunFailed(new InvalidOperationException("release boom"), T0.AddSeconds(60), processed: 5, failed: 2);

        Assert.Equal(
            new VerificationJobSnapshot(null, T0, VerificationJobOutcome.Failed, 5, 2, T0.AddSeconds(60), "InvalidOperationException: release boom"),
            heartbeat.Current);
    }
}
