using FlightAssistant.API.HostedServices;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// <c>SwitchedOff</c> (#471): once the background loops keep running and skip work per tick
/// instead of terminating (later tasks), a heartbeat left at <c>NeverRun</c> while visibly alive
/// would read as never having started, and marking ordinary runs it isn't performing would
/// overwrite the last real counts with zeroes. This third outcome — and the fact that it leaves
/// the previous run's counts untouched — is what these tests pin.
/// </summary>
public class HeartbeatSwitchedOffTests
{
    [Fact]
    public void FlightSync_MarkSwitchedOff_ReportsSwitchedOff()
    {
        var heartbeat = new FlightSyncHeartbeat();

        heartbeat.MarkSwitchedOff();

        Assert.Equal(FlightSyncOutcome.SwitchedOff, heartbeat.Current.LastOutcome);
    }

    // The counts describe the last real run. A switched-off tick did no work, so it must not
    // overwrite them with zeroes and claim a run that processed nothing.
    [Fact]
    public void FlightSync_MarkSwitchedOff_LeavesTheLastRunsCountsAlone()
    {
        var heartbeat = new FlightSyncHeartbeat();
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunSucceeded(processed: 7, failed: 1, nextRunDueUtc: DateTime.UtcNow);

        heartbeat.MarkSwitchedOff();

        Assert.Equal(7, heartbeat.Current.FlightsProcessed);
        Assert.Equal(1, heartbeat.Current.FlightsFailed);
    }

    [Fact]
    public void Verification_MarkSwitchedOff_IsNotPaused()
    {
        var heartbeat = new VerificationJobHeartbeat(TimeProvider.System);

        heartbeat.MarkSwitchedOff();

        // Paused means the cycle ran and held work back for budget. An operator stop is a
        // different situation with a different remedy, and must not read as the same thing.
        Assert.Equal(VerificationJobOutcome.SwitchedOff, heartbeat.Current.LastOutcome);
        Assert.NotEqual(VerificationJobOutcome.Paused, heartbeat.Current.LastOutcome);
    }
}
