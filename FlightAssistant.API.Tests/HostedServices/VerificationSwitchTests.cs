using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Application.Verification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// Covers the admin-controlled switch check added to <see cref="ReferenceVerificationBackgroundService"/>'s
/// loop (#471), on top of the pre-existing config ceiling that
/// <see cref="ReferenceVerificationBackgroundServiceTests"/> already covers and continues to cover
/// unchanged. A wrong decision here is invisible -- nobody sees a stack trace, they see an unexpected
/// AeroAPI bill, or a verification queue that silently stops draining -- so
/// <see cref="SwitchedOff_RunsNoCycle"/> is the one test in this file that must never be allowed to pass
/// for the wrong reason (this task's report records the proof that removing the guard fails it).
/// </summary>
public class VerificationSwitchTests
{
    // Kept as its own constant, rather than read off a constructed VerificationOptions, so
    // StartAndTickAsync's clock advances always agree with whatever value BuildCore gives the
    // service, independent of VerificationOptions's own default ever changing.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    // Set by BuildCore to whichever fakes are wired into the most recently built service, so
    // StartAndTickAsync and ConfigDisabled_NeverReadsTheSwitch can inspect them without every Build
    // overload having to thread them back out individually.
    private FakeJobSwitch _jobSwitch = null!;
    private VerificationJobHeartbeat _heartbeat = null!;

    // ── Fakes ──

    /// <summary>
    /// A switch whose answer and read count a test can inspect directly, standing in for the real
    /// cached <c>IJobSwitch</c> implementation, which is out of scope here -- these tests are about
    /// the loop's reaction to the answer, not how the answer is produced.
    /// </summary>
    private sealed class FakeJobSwitch(bool enabled) : IJobSwitch
    {
        public bool Enabled { get; set; } = enabled;

        /// <summary>How many times the loop has asked. Only the config-disabled test reads this
        /// (asserting it stays zero); ticking tests wait on the heartbeat instead (see
        /// <see cref="StartAndTickAsync"/>), since the switch read alone doesn't prove the rest of
        /// the tick's work has finished.</summary>
        public int Reads { get; private set; }

        public Task<bool> IsEnabledAsync(string job, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Enabled);
        }

        public Task<JobSwitchState> GetAsync(string job, CancellationToken ct) =>
            throw new NotSupportedException("The loop only ever calls IsEnabledAsync; GetAsync backs the admin read endpoint.");
    }

    /// <summary>
    /// Records every cycle the loop ran, standing in for <see cref="IVerificationCycleRunner"/> --
    /// the loop only branches on whether <see cref="VerificationCycleResult.Paused"/> is positive, so
    /// an all-zero result is enough for every test here, which cares only about whether a cycle
    /// reached this runner at all, the same "did a call get through" question
    /// <c>RecordingMediator.Sent</c> answers for the flight-sync loop.
    /// </summary>
    private sealed class RecordingCycleRunner : IVerificationCycleRunner
    {
        private static readonly VerificationCycleResult EmptyResult = new(
            StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 0, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0);

        public List<object> Sent { get; } = [];

        public Task<VerificationCycleResult> RunCycleAsync(CancellationToken ct = default)
        {
            Sent.Add(EmptyResult);
            return Task.FromResult(EmptyResult);
        }
    }

    // ── Building the service under test ──

    private (ReferenceVerificationBackgroundService Service, RecordingCycleRunner Mediator, FakeTimeProvider Time) BuildCore(
        FakeJobSwitch? jobSwitch, bool switchEnabled, bool configEnabled, out VerificationJobHeartbeat heartbeat)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var mediator = new RecordingCycleRunner();
        heartbeat = new VerificationJobHeartbeat(time);
        _heartbeat = heartbeat;
        _jobSwitch = jobSwitch ?? new FakeJobSwitch(switchEnabled);

        // AdminContext is what RunCycleAsync's VerificationJobActor.ApplyTo call needs resolvable in
        // each cycle's scope; which actor it applies is irrelevant to every test in this file.
        var services = new ServiceCollection();
        services.AddScoped<AdminContext>();
        services.AddScoped<IVerificationCycleRunner>(_ => mediator);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var service = new ReferenceVerificationBackgroundService(
            scopeFactory,
            Options.Create(new VerificationOptions { Enabled = configEnabled, PollInterval = PollInterval }),
            heartbeat,
            time,
            NullLogger<ReferenceVerificationBackgroundService>.Instance,
            _jobSwitch);

        return (service, mediator, time);
    }

    private (ReferenceVerificationBackgroundService Service, RecordingCycleRunner Mediator, FakeTimeProvider Time) Build(
        bool switchEnabled = true, bool configEnabled = true) =>
        BuildCore(null, switchEnabled, configEnabled, out _);

    private (ReferenceVerificationBackgroundService Service, RecordingCycleRunner Mediator, FakeTimeProvider Time) Build(
        FakeJobSwitch jobSwitch, bool configEnabled = true) =>
        BuildCore(jobSwitch, switchEnabled: true, configEnabled, out _);

    private (ReferenceVerificationBackgroundService Service, RecordingCycleRunner Mediator, FakeTimeProvider Time) Build(
        bool switchEnabled, out VerificationJobHeartbeat heartbeat) =>
        BuildCore(null, switchEnabled, configEnabled: true, out heartbeat);

    // Parameters are deliberately (configEnabled, switchEnabled) here, matching the order the one
    // caller below names them in: a named argument that is not in its declared position cannot be
    // followed by the unnamed `out heartbeat` argument (CS8323).
    private (ReferenceVerificationBackgroundService Service, RecordingCycleRunner Mediator, FakeTimeProvider Time) Build(
        bool configEnabled, bool switchEnabled, out VerificationJobHeartbeat heartbeat) =>
        BuildCore(null, switchEnabled, configEnabled, out heartbeat);

    /// <summary>
    /// Drives <paramref name="ticks"/> loop passes. The first call starts the service -- the while
    /// loop's first pass runs immediately, before any tick is due -- and every later pass advances
    /// <paramref name="time"/> by <see cref="PollInterval"/>.
    ///
    /// <para>
    /// <b>Why this waits on the heartbeat, not <see cref="FakeJobSwitch.Reads"/>.</b>
    /// <c>BackgroundService.StartAsync</c> hands <c>ExecuteAsync</c> to the thread pool rather than
    /// running it out on the calling thread (confirmed by instrumentation while diagnosing this: the
    /// switch read and the cycle run on a worker thread, not the test's), so a pass resumes
    /// genuinely concurrently with this method, not merely "later on the same thread". Reading the
    /// switch happens first and fast; the tests only care whether that changed anything the pass
    /// reaches after, so reads alone raced ahead of a switched-on pass's later work in practice --
    /// resolving <c>AdminContext</c> and <c>IVerificationCycleRunner</c> from the DI scope is enough
    /// extra latency to occasionally still be in flight when a switch-read-only wait had already
    /// returned. Every pass, switched on or off, ends by publishing exactly one new
    /// <see cref="VerificationJobHeartbeat.Current"/> snapshot whose outcome is not
    /// <see cref="VerificationJobOutcome.Running"/> (<c>MarkSwitchedOff</c>, or
    /// <c>MarkRunStarted</c> then a terminal <c>Mark*</c>) -- and <c>with</c> always allocates a new
    /// record, even when a repeated <c>MarkSwitchedOff</c> leaves every field equal to the last
    /// snapshot's -- so comparing snapshot references (not the records' own value equality) is a
    /// generic, tick-agnostic "this pass's work, including any cycle, is fully done" signal.
    /// </para>
    /// </summary>
    private async Task StartAndTickAsync(ReferenceVerificationBackgroundService service, FakeTimeProvider time, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            var before = _heartbeat.Current;
            if (service.ExecuteTask is null)
                await service.StartAsync(CancellationToken.None);
            else
                time.Advance(PollInterval);
            await WaitUntil(() => !ReferenceEquals(_heartbeat.Current, before)
                && _heartbeat.Current.LastOutcome != VerificationJobOutcome.Running);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 5 s.");
            await Task.Delay(10);
        }
    }

    // ── Tests ──

    // The whole point: a switched-off loop must not spend money.
    [Fact]
    public async Task SwitchedOff_RunsNoCycle()
    {
        var (service, mediator, time) = Build(switchEnabled: false);

        await StartAndTickAsync(service, time, ticks: 3);

        // No cycle ran, so nothing reached the runner and no AeroAPI call could have happened.
        Assert.Empty(mediator.Sent);
    }

    [Fact]
    public async Task SwitchedOff_MarksTheHeartbeatSwitchedOff()
    {
        var (service, _, time) = Build(switchEnabled: false, out var heartbeat);

        await StartAndTickAsync(service, time, ticks: 1);

        Assert.Equal(VerificationJobOutcome.SwitchedOff, heartbeat.Current.LastOutcome);
    }

    // SwitchedOff and Paused are different situations. A switched-off cycle never ran; a paused one
    // ran and held work back because the AeroAPI budget is exhausted. Reporting one as the other
    // sends an operator to the wrong remedy.
    [Fact]
    public async Task SwitchedOff_DoesNotReportPaused()
    {
        var (service, _, time) = Build(switchEnabled: false, out var heartbeat);

        await StartAndTickAsync(service, time, ticks: 1);

        Assert.NotEqual(VerificationJobOutcome.Paused, heartbeat.Current.LastOutcome);
    }

    // A loop that exits when switched off can never be switched back on.
    [Fact]
    public async Task SwitchedBackOn_ResumesWithoutARestart()
    {
        var jobSwitch = new FakeJobSwitch(enabled: false);
        var (service, mediator, time) = Build(jobSwitch);
        await StartAndTickAsync(service, time, ticks: 2);
        Assert.Empty(mediator.Sent);

        jobSwitch.Enabled = true;
        await StartAndTickAsync(service, time, ticks: 1);

        Assert.NotEmpty(mediator.Sent);
    }

    [Fact]
    public async Task SwitchedOn_RunsTheCycleAsBefore()
    {
        var (service, mediator, time) = Build(switchEnabled: true);

        await StartAndTickAsync(service, time, ticks: 1);

        Assert.NotEmpty(mediator.Sent);
    }

    // The config ceiling keeps its current behaviour exactly: the service terminates, and the
    // heartbeat stays NeverRun so /admin/health reads "disabled" rather than "broken".
    [Fact]
    public async Task ConfigDisabled_TerminatesTheServiceAndLeavesTheHeartbeatNeverRun()
    {
        var (service, mediator, _) = Build(configEnabled: false, switchEnabled: true, out var heartbeat);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.Empty(mediator.Sent);
        Assert.Equal(VerificationJobOutcome.NeverRun, heartbeat.Current.LastOutcome);
    }

    // A config-disabled service must not read the switch at all — there is nothing it could change.
    [Fact]
    public async Task ConfigDisabled_NeverReadsTheSwitch()
    {
        var jobSwitch = new FakeJobSwitch(enabled: true);
        var (service, _, _) = Build(jobSwitch, configEnabled: false);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.Equal(0, jobSwitch.Reads);
    }
}
