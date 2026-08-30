using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// <see cref="AeroApiThrottleGate"/> is the single shared rate limiter for every AeroAPI
/// <see cref="HttpClient"/> — the personal tier allows only ~5 queries/minute, and that budget is
/// process-wide, not per client. These tests use a <see cref="FakeTimeProvider"/> so spacing is
/// proven without any real sleeping.
/// </summary>
public class AeroApiThrottleGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task WaitForTurnAsync_DoesNotDelay_TheFirstCall()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AeroApiThrottleGate(clock);

        var task = gate.WaitForTurnAsync(Interval, CancellationToken.None);

        // No time advance needed — the very first reservation has nothing to wait behind.
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task WaitForTurnAsync_DelaysTheSecondCall_UntilMinIntervalHasElapsed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AeroApiThrottleGate(clock);

        await gate.WaitForTurnAsync(Interval, CancellationToken.None);

        var second = gate.WaitForTurnAsync(Interval, CancellationToken.None);
        Assert.False(second.IsCompleted);

        // Advancing by less than the interval must not release it yet.
        clock.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.False(second.IsCompleted);

        // The remaining second closes the gap.
        clock.Advance(TimeSpan.FromSeconds(1));
        await second;
    }

    [Fact]
    public async Task WaitForTurnAsync_NeverSleepsForReal()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AeroApiThrottleGate(clock);

        var startedAt = DateTime.UtcNow;
        await gate.WaitForTurnAsync(Interval, CancellationToken.None);

        var second = gate.WaitForTurnAsync(Interval, CancellationToken.None);
        clock.Advance(Interval);
        await second;

        // Wall-clock time elapsed should be negligible even though 12s of *simulated* time passed.
        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WaitForTurnAsync_QueuesThirdCall_BehindTheSecond()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AeroApiThrottleGate(clock);

        await gate.WaitForTurnAsync(Interval, CancellationToken.None); // t=0
        var second = gate.WaitForTurnAsync(Interval, CancellationToken.None); // waits until t=12s
        var third = gate.WaitForTurnAsync(Interval, CancellationToken.None); // waits until t=24s

        clock.Advance(Interval);
        await second;
        Assert.False(third.IsCompleted);

        clock.Advance(Interval);
        await third;
    }
}
