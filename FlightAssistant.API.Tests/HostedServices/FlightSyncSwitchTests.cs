using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Jobs;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// Covers the admin-controlled switch check added to <see cref="FlightStatusSyncBackgroundService"/>'s
/// loop (#471), on top of the pre-existing config ceiling that
/// <see cref="FlightStatusSyncBackgroundServiceTests"/> already covers and continues to cover
/// unchanged. A wrong decision here is invisible -- nobody sees a stack trace, they see an
/// unexpected AeroAPI bill, or stale flight data nobody noticed for a week -- so
/// <see cref="SwitchedOff_RunsNoCycle"/> is the one test in this file that must never be allowed to
/// pass for the wrong reason (task 5's report records the proof that removing the guard fails it).
/// </summary>
public class FlightSyncSwitchTests
{
    // Kept as its own constant, rather than read off a constructed FlightSyncOptions, so
    // StartAndTickAsync's clock advances always agree with whatever value BuildCore gives the
    // service, independent of FlightSyncOptions's own default ever changing.
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMinutes(1);

    // Set by BuildCore to whichever FakeJobSwitch ends up wired into the most recently built
    // service, so StartAndTickAsync can confirm a tick has actually landed before returning: every
    // iteration reads the switch exactly once, whether it answers on or off, which is the one signal
    // common to all six tests below -- several of which never touch the heartbeat or MediatR.
    private FakeJobSwitch _jobSwitch = null!;

    // ── Fakes ──

    /// <summary>
    /// A switch whose answer and read count a test can inspect directly, standing in for
    /// <c>CachedJobSwitch</c>'s caching/DB behaviour, which is out of scope here -- these tests are
    /// about the loop's reaction to the answer, not how the answer is produced.
    /// </summary>
    private sealed class FakeJobSwitch(bool enabled) : IJobSwitch
    {
        public bool Enabled { get; set; } = enabled;

        /// <summary>How many times the loop has asked. The config-disabled tests assert this stays
        /// zero; <see cref="StartAndTickAsync"/> uses it to know a tick actually ran.</summary>
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
    /// Records every request handed to it without stubbing a response -- the loop discards whatever
    /// MediatR returns, so for these tests only "did a cycle reach MediatR at all" matters. A
    /// hand-written fake rather than an NSubstitute mock so <see cref="Sent"/> reads as plainly as
    /// <see cref="FakeJobSwitch.Reads"/> above.
    /// </summary>
    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(default(TResponse)!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Sent.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The loop only ever calls Send.");

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The loop only ever calls Send.");

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The loop only ever calls Send.");

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification =>
            throw new NotSupportedException("The loop only ever calls Send.");
    }

    // ── Building the service under test ──

    private (FlightStatusSyncBackgroundService Service, RecordingMediator Mediator, FakeTimeProvider Time) BuildCore(
        FakeJobSwitch? jobSwitch, bool switchEnabled, bool configEnabled, out FlightSyncHeartbeat heartbeat)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var mediator = new RecordingMediator();
        heartbeat = new FlightSyncHeartbeat();
        _jobSwitch = jobSwitch ?? new FakeJobSwitch(switchEnabled);

        // One due flight, so a switch-on tick has something to send through MediatR; which flight
        // is irrelevant to every test in this file.
        var flights = Substitute.For<IFlightRepository>();
        flights.GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>()).Returns(new List<int> { 1 });

        var services = new ServiceCollection();
        services.AddScoped(_ => flights);
        services.AddScoped<IMediator>(_ => mediator);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var service = new FlightStatusSyncBackgroundService(
            scopeFactory,
            Options.Create(new FlightSyncOptions { Enabled = configEnabled, LoopInterval = LoopInterval }),
            time,
            NullLogger<FlightStatusSyncBackgroundService>.Instance,
            heartbeat,
            _jobSwitch);

        return (service, mediator, time);
    }

    private (FlightStatusSyncBackgroundService Service, RecordingMediator Mediator, FakeTimeProvider Time) Build(
        bool switchEnabled = true, bool configEnabled = true) =>
        BuildCore(null, switchEnabled, configEnabled, out _);

    private (FlightStatusSyncBackgroundService Service, RecordingMediator Mediator, FakeTimeProvider Time) Build(
        FakeJobSwitch jobSwitch, bool configEnabled = true) =>
        BuildCore(jobSwitch, switchEnabled: true, configEnabled, out _);

    private (FlightStatusSyncBackgroundService Service, RecordingMediator Mediator, FakeTimeProvider Time) Build(
        bool switchEnabled, out FlightSyncHeartbeat heartbeat) =>
        BuildCore(null, switchEnabled, configEnabled: true, out heartbeat);

    // Parameters are deliberately (configEnabled, switchEnabled) here, matching the order the one
    // caller below names them in: a named argument that is not in its declared position cannot be
    // followed by the unnamed `out heartbeat` argument (CS8323).
    private (FlightStatusSyncBackgroundService Service, RecordingMediator Mediator, FakeTimeProvider Time) Build(
        bool configEnabled, bool switchEnabled, out FlightSyncHeartbeat heartbeat) =>
        BuildCore(null, switchEnabled, configEnabled, out heartbeat);

    /// <summary>
    /// Drives <paramref name="ticks"/> loop passes. The first call starts the service -- the
    /// do-while's first pass runs immediately, before any tick is due -- and every later pass
    /// advances <paramref name="time"/> by <see cref="LoopInterval"/>. Either way, a pass resumes
    /// asynchronously (via the thread pool, not inline on this thread: <c>BackgroundService</c>
    /// hands <c>ExecuteAsync</c> a task rather than running it out, and <c>PeriodicTimer</c>'s
    /// continuation is posted rather than run from inside <c>Advance</c>), so each one is followed
    /// by a bounded wait for <see cref="FakeJobSwitch.Reads"/> to climb -- the one thing every pass
    /// does, whichever way the switch answers, which is what makes it a safe generic signal that a
    /// pass has actually landed before the next line of the test inspects the result.
    /// </summary>
    private async Task StartAndTickAsync(FlightStatusSyncBackgroundService service, FakeTimeProvider time, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            var readsBefore = _jobSwitch.Reads;
            if (service.ExecuteTask is null)
                await service.StartAsync(CancellationToken.None);
            else
                time.Advance(LoopInterval);
            await WaitUntil(() => _jobSwitch.Reads > readsBefore);
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

        // No cycle ran, so nothing was sent through MediatR and no AeroAPI call could have happened.
        Assert.Empty(mediator.Sent);
    }

    [Fact]
    public async Task SwitchedOff_MarksTheHeartbeatSwitchedOff()
    {
        var (service, _, time) = Build(switchEnabled: false, out var heartbeat);

        await StartAndTickAsync(service, time, ticks: 1);

        Assert.Equal(FlightSyncOutcome.SwitchedOff, heartbeat.Current.LastOutcome);
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
        var (service, mediator, time) = Build(configEnabled: false, switchEnabled: true, out var heartbeat);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;   // completes immediately when config-disabled

        Assert.Empty(mediator.Sent);
        Assert.Equal(FlightSyncOutcome.NeverRun, heartbeat.Current.LastOutcome);
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
