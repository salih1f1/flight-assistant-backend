using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Application.Verification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// <see cref="ReferenceVerificationBackgroundService"/>'s loop, run directly (the integration factory
/// removes hosted services) against a scripted <see cref="IVerificationCycleRunner"/> and a
/// <see cref="FakeTimeProvider"/>, so the poll interval elapses only when a test advances it.
/// </summary>
public class ReferenceVerificationBackgroundServiceTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(T0));

    private static VerificationCycleResult Processed(
        int processed, int failed = 0, int stuckAbandoned = 0, int failuresUnrecorded = 0, int paused = 0) =>
        new(StuckReset: 0, StuckAbandoned: stuckAbandoned, RequestsProcessed: processed, RequestsFailed: failed, ClaimsLost: 0,
            FailuresUnrecorded: failuresUnrecorded, Paused: paused);

    [Fact]
    public async Task Disabled_NeverRunsACycle_AndTheHeartbeatStaysNeverRun()
    {
        var script = new CycleScript();
        await using var job = Job.Create(script, _time, enabled: false);

        await job.Service.StartAsync(CancellationToken.None);
        await job.Service.ExecuteTask!.WaitAsync(WaitLimit);
        _time.Advance(PollInterval * 10);

        Assert.Equal(0, script.Calls);
        Assert.Equal(
            new VerificationJobSnapshot(null, null, VerificationJobOutcome.NeverRun, 0, 0, 0, 0, null, null),
            job.Heartbeat.Current);

        await job.Service.StopAsync(CancellationToken.None);
    }

    /// <summary>Both new counts travel from the cycle result to the heartbeat on the success path,
    /// not just <c>RequestsProcessed</c>/<c>RequestsFailed</c> (R5).</summary>
    [Fact]
    public async Task ACycle_MarksTheHeartbeatSuccessWithItsCounts_AndRunsInAScopeCarryingTheSystemActor()
    {
        var script = new CycleScript();
        script.Then(() => new VerificationCycleResult(
            StuckReset: 1, StuckAbandoned: 3, RequestsProcessed: 4, RequestsFailed: 1, ClaimsLost: 1, FailuresUnrecorded: 2));
        await using var job = Job.Create(script, _time);

        await job.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Success);

        Assert.Equal(
            new VerificationJobSnapshot(T0, T0, VerificationJobOutcome.Success, 4, 1, 3, 2, T0 + PollInterval, null),
            job.Heartbeat.Current);
        Assert.Equal(("system", "Verification job"), Assert.Single(script.Actors));

        await job.Service.StopAsync(CancellationToken.None);
    }

    /// <summary>A cycle result carrying a paused count marks the heartbeat Paused, not Success -- R4/R5
    /// (#401 task 2): a paused cycle is not a failed cycle either, so this must not reach Failed.</summary>
    [Fact]
    public async Task ACycleThatPaused_MarksTheHeartbeatPaused_NotSuccessOrFailed()
    {
        var script = new CycleScript();
        script.Then(() => Processed(processed: 5, failed: 1, paused: 3));
        await using var job = Job.Create(script, _time);

        await job.Service.StartAsync(CancellationToken.None);
        // One read per poll, and the asserts use the snapshot that ended the wait (#438). The old
        // condition read Current twice, so NeverRun on the first read and Running on the second
        // satisfied both halves while the cycle was still running.
        var snapshot = await WaitForSnapshotAsync(job.Heartbeat,
            s => s.LastOutcome is not (VerificationJobOutcome.Running or VerificationJobOutcome.NeverRun));
        Assert.Equal(VerificationJobOutcome.Paused, snapshot.LastOutcome);
        Assert.Equal(5, snapshot.RequestsProcessed);
        Assert.Equal(1, snapshot.RequestsFailed);
        Assert.Equal(3, snapshot.RequestsPaused);
        Assert.Null(snapshot.LastError);

        await job.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailingCycle_MarksTheHeartbeatFailed_AndTheLoopRunsAgainAfterThePollInterval()
    {
        var script = new CycleScript();
        script.Then(() => throw new InvalidOperationException("cycle boom"));
        script.Then(() => Processed(2));
        await using var job = Job.Create(script, _time);

        await job.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Failed);

        var failed = job.Heartbeat.Current;
        Assert.Equal("InvalidOperationException: cycle boom", failed.LastError);
        Assert.Equal(T0 + PollInterval, failed.NextRunDueUtc);
        Assert.Equal(1, script.Calls);

        await AdvanceUntilAsync(() => script.Calls >= 2);
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Success);

        var recovered = job.Heartbeat.Current;
        Assert.Equal(2, recovered.RequestsProcessed);
        Assert.Null(recovered.LastError);

        await job.Service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// A cycle that fails only after processing its batch (the runner's
    /// <see cref="VerificationCycleFailedException"/>) is Failed with that batch's counts, new fields
    /// included (R5). The previous cycle reported different counts for every field, so keeping any of
    /// those would fail this test. The failed batch also paused some requests before releasing source
    /// caches threw (#401 task 2): that count travels to the heartbeat too, but the outcome is still
    /// Failed, not Paused -- the release failure is what determines the outcome here.
    /// </summary>
    [Fact]
    public async Task ACycleFailingAfterItsBatch_MarksTheHeartbeatFailed_WithThatBatchsCounts()
    {
        var script = new CycleScript();
        script.Then(() => Processed(9, failed: 4, stuckAbandoned: 3, failuresUnrecorded: 2));
        script.Then(() => throw new VerificationCycleFailedException(
            Processed(5, failed: 2, stuckAbandoned: 1, failuresUnrecorded: 0, paused: 3), new InvalidOperationException("release boom")));
        await using var job = Job.Create(script, _time);

        await job.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Success);
        Assert.Equal(9, job.Heartbeat.Current.RequestsProcessed);

        await AdvanceUntilAsync(() => script.Calls >= 2);
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Failed);

        var snapshot = job.Heartbeat.Current;
        Assert.Equal(5, snapshot.RequestsProcessed);
        Assert.Equal(2, snapshot.RequestsFailed);
        Assert.Equal(1, snapshot.StuckAbandoned);
        Assert.Equal(0, snapshot.FailuresUnrecorded);
        Assert.Equal(3, snapshot.RequestsPaused);
        Assert.Equal("InvalidOperationException: release boom", snapshot.LastError);

        await job.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoppingDuringThePollDelay_EndsTheLoopCleanly_WithoutMarkingTheCycleFailed()
    {
        var script = new CycleScript();
        await using var job = Job.Create(script, _time);

        await job.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Success);
        await job.Service.StopAsync(CancellationToken.None);

        await job.Service.ExecuteTask!.WaitAsync(WaitLimit);
        Assert.True(job.Service.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(VerificationJobOutcome.Success, job.Heartbeat.Current.LastOutcome);
    }

    /// <summary>
    /// Stopping while a cycle is still running: the runner observes the stopping token and throws. That
    /// is a clean shutdown, not a failed cycle, so the heartbeat is left Running, never marked Failed.
    /// A runner that finishes synchronously only reaches the poll delay's cancellation (the test above).
    /// </summary>
    [Fact]
    public async Task StoppingDuringACycle_EndsTheLoopCleanly_WithoutMarkingTheCycleFailed()
    {
        var script = new CycleScript();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        script.ThenAsync(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Processed(0);
        });
        await using var job = Job.Create(script, _time);

        await job.Service.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(WaitLimit);
        await job.Service.StopAsync(CancellationToken.None);

        await job.Service.ExecuteTask!.WaitAsync(WaitLimit);
        Assert.True(job.Service.ExecuteTask.IsCompletedSuccessfully);
        var snapshot = job.Heartbeat.Current;
        Assert.Equal(VerificationJobOutcome.Running, snapshot.LastOutcome);
        Assert.Null(snapshot.LastError);
        Assert.Null(snapshot.LastRunCompletedUtc);
        Assert.Equal(1, script.Calls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The background service did not reach the expected state in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Polls <paramref name="heartbeat"/> until one snapshot satisfies <paramref name="predicate"/>, and
    /// returns that snapshot (#438). It reads <see cref="VerificationJobHeartbeat.Current"/> exactly once
    /// per poll. Each access can return a newer snapshot, so a condition that reads it twice can pass on
    /// two different states, and re-reading it after the wait can return a later state.
    /// </summary>
    private static async Task<VerificationJobSnapshot> WaitForSnapshotAsync(
        VerificationJobHeartbeat heartbeat, Func<VerificationJobSnapshot, bool> predicate)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (true)
        {
            var snapshot = heartbeat.Current;
            if (predicate(snapshot)) return snapshot;
            if (DateTime.UtcNow > deadline) Assert.Fail("The background service did not reach the expected state in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Advances fake time by the poll interval until <paramref name="cycleStarted"/> holds, and not once
    /// more. The loop may not have started its delay when a test first advances, so this keeps
    /// advancing until the next cycle has <i>started</i> (the script's call count). Waiting for a
    /// cycle's <i>outcome</i> instead would let a late advance start a further cycle that overwrites
    /// the counts under test; the caller waits for the outcome with <see cref="WaitUntilAsync"/>, which
    /// never advances.
    /// </summary>
    private async Task AdvanceUntilAsync(Func<bool> cycleStarted)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!cycleStarted())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The background service did not start another cycle in time.");
            _time.Advance(PollInterval);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// What successive cycles return or throw. Once the script is used up, every later cycle repeats the
    /// last one, so a stray extra cycle cannot change what the heartbeat reports; an empty script
    /// returns an empty result.
    /// </summary>
    private sealed class CycleScript
    {
        private readonly object _lock = new();
        private readonly Queue<Func<CancellationToken, Task<VerificationCycleResult>>> _cycles = new();
        private Func<CancellationToken, Task<VerificationCycleResult>>? _last;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public List<(string? ActorId, string? ActorName)> Actors { get; } = new();

        public void Then(Func<VerificationCycleResult> cycle) => ThenAsync(_ => Task.FromResult(cycle()));

        public void ThenAsync(Func<CancellationToken, Task<VerificationCycleResult>> cycle)
        {
            lock (_lock) _cycles.Enqueue(cycle);
        }

        public Task<VerificationCycleResult> NextAsync(IAdminContext adminContext, CancellationToken ct)
        {
            Func<CancellationToken, Task<VerificationCycleResult>>? cycle;
            lock (_lock)
            {
                Actors.Add((adminContext.ActorId, adminContext.ActorName));
                if (_cycles.Count > 0) _last = _cycles.Dequeue();
                cycle = _last;
            }
            Interlocked.Increment(ref _calls);
            return cycle is null ? Task.FromResult(Processed(0)) : cycle(ct);
        }
    }

    private sealed class ScriptedRunner(CycleScript script, IAdminContext adminContext) : IVerificationCycleRunner
    {
        public Task<VerificationCycleResult> RunCycleAsync(CancellationToken ct = default) => script.NextAsync(adminContext, ct);
    }

    /// <summary>Always-on stub for the constructor's <see cref="IJobSwitch"/> parameter (#471): this
    /// file is entirely about the config ceiling and the cycle's own behaviour, never about the
    /// switch, which <c>VerificationSwitchTests</c> covers on its own.</summary>
    private sealed class AlwaysOnJobSwitch : IJobSwitch
    {
        public Task<bool> IsEnabledAsync(string job, CancellationToken ct) => Task.FromResult(true);

        public Task<JobSwitchState> GetAsync(string job, CancellationToken ct) =>
            throw new NotSupportedException("The loop only ever calls IsEnabledAsync.");
    }

    private sealed class Job : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Job(ServiceProvider provider, ReferenceVerificationBackgroundService service, VerificationJobHeartbeat heartbeat)
        {
            _provider = provider;
            Service = service;
            Heartbeat = heartbeat;
        }

        public ReferenceVerificationBackgroundService Service { get; }
        public VerificationJobHeartbeat Heartbeat { get; }

        public static Job Create(CycleScript script, TimeProvider time, bool enabled = true)
        {
            var services = new ServiceCollection();
            services.AddScoped<AdminContext>();
            services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
            services.AddScoped<IVerificationCycleRunner>(sp => new ScriptedRunner(script, sp.GetRequiredService<IAdminContext>()));
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            var heartbeat = new VerificationJobHeartbeat(time);
            var service = new ReferenceVerificationBackgroundService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new VerificationOptions { Enabled = enabled, PollInterval = PollInterval }),
                heartbeat,
                time,
                NullLogger<ReferenceVerificationBackgroundService>.Instance,
                new AlwaysOnJobSwitch());

            return new Job(provider, service, heartbeat);
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
