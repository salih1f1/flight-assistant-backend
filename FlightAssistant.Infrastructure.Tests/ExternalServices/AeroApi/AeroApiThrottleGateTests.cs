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
    public async Task WaitForTurnAsync_ReleasesTheSlot_WhenTheWaitIsCancelled()
    {
        // The amplifier behind the resilience-timeout defect: a reservation used to advance the
        // queue at reservation time and was never given back if the caller's wait was cancelled
        // (a per-attempt timeout, a dropped request). Every abandoned attempt therefore pushed the
        // gate a further interval out while nothing had actually been sent, so each retry made the
        // next caller strictly worse than the one before it.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AeroApiThrottleGate(clock);

        await gate.WaitForTurnAsync(Interval, CancellationToken.None); // t=0, sends

        using var cts = new CancellationTokenSource();
        var abandoned = gate.WaitForTurnAsync(Interval, cts.Token); // would send at t=12
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        // The next caller inherits the abandoned slot at t=12 — NOT t=24. Spacing from the send
        // that really happened is preserved; the slot nobody used is not charged to anyone.
        var next = gate.WaitForTurnAsync(Interval, CancellationToken.None);
        clock.Advance(Interval);
        await next;
    }

    [Fact]
    public async Task WaitForTurnAsync_KeepsSpacing_WhenAnAbandonedCallerIsNotTheLatestReservation()
    {
        // Releasing must not rewind past a reservation someone else made behind us: that caller has
        // already been spaced off our slot, and moving the gate back would let it send too early.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AeroApiThrottleGate(clock);

        await gate.WaitForTurnAsync(Interval, CancellationToken.None); // t=0, sends

        using var cts = new CancellationTokenSource();
        var abandoned = gate.WaitForTurnAsync(Interval, cts.Token); // holds t=12
        var behind = gate.WaitForTurnAsync(Interval, CancellationToken.None); // holds t=24

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        clock.Advance(Interval);
        Assert.False(behind.IsCompleted); // still waiting for its own t=24 slot

        clock.Advance(Interval);
        await behind;
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

    [Fact]
    public void NextSlotUtc_IsNull_BeforeAnyReservation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AeroApiThrottleGate(clock);

        Assert.Null(gate.NextSlotUtc);
    }

    [Fact]
    public async Task NextSlotUtc_ReflectsTheReservedSlot_AfterAReservation()
    {
        var start = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(start);
        var gate = new AeroApiThrottleGate(clock);

        await gate.WaitForTurnAsync(Interval, CancellationToken.None); // sends immediately at t=0

        // The first send reserves the NEXT slot, one interval out — not the slot it just used.
        Assert.Equal(start + Interval, gate.NextSlotUtc);

        var second = gate.WaitForTurnAsync(Interval, CancellationToken.None); // queues for t=12
        Assert.Equal(start + Interval + Interval, gate.NextSlotUtc);

        clock.Advance(Interval);
        await second;
    }
}
