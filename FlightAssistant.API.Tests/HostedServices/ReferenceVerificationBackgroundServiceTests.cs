using System.Collections.Concurrent;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Administration;
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
            new VerificationJobSnapshot(null, null, VerificationJobOutcome.NeverRun, 0, 0, null, null),
            job.Heartbeat.Current);

        await job.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ACycle_MarksTheHeartbeatSuccessWithItsCounts_AndRunsInAScopeCarryingTheSystemActor()
    {
        var script = new CycleScript();
        script.Then(() => new VerificationCycleResult(StuckReset: 1, StuckAbandoned: 0, RequestsProcessed: 4, RequestsFailed: 1, ClaimsLost: 1, FailuresUnrecorded: 0));
        await using var job = Job.Create(script, _time);

        await job.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Success);

        Assert.Equal(
            new VerificationJobSnapshot(T0, T0, VerificationJobOutcome.Success, 4, 1, T0 + PollInterval, null),
            job.Heartbeat.Current);
        Assert.Equal(("system", "Verification job"), Assert.Single(script.Actors));

        await job.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailingCycle_MarksTheHeartbeatFailed_AndTheLoopRunsAgainAfterThePollInterval()
    {
        var script = new CycleScript();
        script.Then(() => throw new InvalidOperationException("cycle boom"));
        script.Then(() => new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 2, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0));
        await using var job = Job.Create(script, _time);

        await job.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Failed);

        var failed = job.Heartbeat.Current;
        Assert.Equal("InvalidOperationException: cycle boom", failed.LastError);
        Assert.Equal(T0 + PollInterval, failed.NextRunDueUtc);
        Assert.Equal(1, script.Calls);

        await AdvanceUntilAsync(() => job.Heartbeat.Current.LastOutcome == VerificationJobOutcome.Success);

        var recovered = job.Heartbeat.Current;
        Assert.Equal(2, recovered.RequestsProcessed);
        Assert.Null(recovered.LastError);
        Assert.True(script.Calls >= 2);

        await job.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_EndsTheLoopCleanly_WithoutMarkingTheCycleFailed()
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The background service did not reach the expected state in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>Advances fake time by the poll interval until <paramref name="condition"/> holds. The
    /// loop may not have started its delay yet when a test first advances, so this keeps advancing.</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The background service did not run another cycle in time.");
            _time.Advance(PollInterval);
            await Task.Delay(10);
        }
    }

    /// <summary>What each successive cycle returns or throws; later cycles return an empty result.</summary>
    private sealed class CycleScript
    {
        private readonly ConcurrentQueue<Func<VerificationCycleResult>> _cycles = new();
        private int _calls;

        public int Calls => _calls;
        public ConcurrentQueue<(string? ActorId, string? ActorName)> Actors { get; } = new();

        public void Then(Func<VerificationCycleResult> cycle) => _cycles.Enqueue(cycle);

        public VerificationCycleResult Next(IAdminContext adminContext)
        {
            Actors.Enqueue((adminContext.ActorId, adminContext.ActorName));
            Interlocked.Increment(ref _calls);
            return _cycles.TryDequeue(out var cycle) ? cycle() : new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 0, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0);
        }
    }

    private sealed class ScriptedRunner(CycleScript script, IAdminContext adminContext) : IVerificationCycleRunner
    {
        public Task<VerificationCycleResult> RunCycleAsync(CancellationToken ct = default) =>
            Task.FromResult(script.Next(adminContext));
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
                NullLogger<ReferenceVerificationBackgroundService>.Instance);

            return new Job(provider, service, heartbeat);
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
