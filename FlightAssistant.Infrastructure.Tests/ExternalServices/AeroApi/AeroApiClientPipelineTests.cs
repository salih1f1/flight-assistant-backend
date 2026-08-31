using System.Net;
using System.Text;
using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Sends real requests through the real named <c>"AeroApi"</c> <see cref="HttpClient"/> built by
/// <c>AddInfrastructure</c> — resilience handler, throttling handler and all — and asserts they
/// reach the transport.
///
/// <para>
/// This is the gap that let a critical defect through 478 passing tests.
/// <c>AeroApiThrottlingHandlerTests</c> builds a bare <c>new HttpClient(handler)</c> with no
/// resilience pipeline around it, and <c>AeroApiDependencyInjectionTests</c> only ever
/// <i>resolves</i> objects without sending anything. Neither could see that the standard resilience
/// handler's 10s default per-attempt timeout was shorter than the 12s throttle interval nested
/// inside it, so every second call in an interval window was killed before it reached the
/// transport, retried twice, and finally failed the total-request timeout — reported to the caller
/// as a 502 after a 30-second hang. Only a request that actually traverses the composed pipeline
/// can catch that.
/// </para>
///
/// <para>
/// A <see cref="FakeTimeProvider"/> is registered before <c>AddInfrastructure</c> (whose own
/// registration is <c>TryAddSingleton</c>, so it defers). Both the throttle gate and Polly's
/// timeout strategies read <see cref="TimeProvider"/> from DI, so the whole pipeline runs on
/// simulated time and these tests never actually sleep.
/// </para>
/// </summary>
public class AeroApiClientPipelineTests
{
    /// <summary>Counts what actually made it past every handler to the wire.</summary>
    private sealed class CountingTransport(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Stands in for <c>DbAeroApiUsageRecorder</c>. <c>AddInfrastructure</c> registers the real one
    /// against <c>FlightAssistantDbContext</c>, which only the API composition root registers — so
    /// in this container every write would throw, be swallowed by the handler's best-effort catch,
    /// and leave "was this call metered?" unobservable. Substituting the seam is what makes the
    /// metering assertions below mean anything.
    /// </summary>
    private sealed class CountingUsageRecorder : IAeroApiUsageRecorder
    {
        private readonly List<AeroApiCall> _calls = [];

        public IReadOnlyList<AeroApiCall> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        public Task RecordAsync(AeroApiCall call, CancellationToken ct)
        {
            lock (_calls) _calls.Add(call);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A database that hangs rather than refuses: the write never completes on its own, only when
    /// the budget the handler gives it runs out. Honouring the token is what Npgsql does when a
    /// command is cancelled, and it is the only thing standing between a stalled pool and a
    /// re-billed AeroAPI query.
    /// </summary>
    private sealed class HangingUsageRecorder : IAeroApiUsageRecorder
    {
        public Task RecordAsync(AeroApiCall call, CancellationToken ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static ServiceProvider BuildContainer(
        FakeTimeProvider clock,
        CountingTransport transport,
        TimeSpan? minRequestInterval = null,
        IAeroApiUsageRecorder? usageRecorder = null)
    {
        var settings = new Dictionary<string, string?> { ["AeroApi:ApiKey"] = "dummy-test-key" };
        if (minRequestInterval is { } interval)
            settings["AeroApi:MinRequestInterval"] = interval.ToString();

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        // Registered before AddInfrastructure so its TryAddSingleton(TimeProvider.System) defers to
        // this one — the whole point being that no wall-clock time passes during these tests.
        services.AddSingleton<TimeProvider>(clock);

        services.AddAppCaching(config);
        services.AddInfrastructure(config, isDevelopment: true);

        // Swap only the innermost transport: every handler AddInfrastructure registered stays in
        // place, which is exactly what is under test.
        services.AddHttpClient("AeroApi").ConfigurePrimaryHttpMessageHandler(() => transport);

        if (usageRecorder is not null)
        {
            services.RemoveAll<IAeroApiUsageRecorder>();
            services.AddSingleton(usageRecorder);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Drives the fake clock forward until the pending request settles, in small simulated steps so
    /// any timer the pipeline registered (the throttle's delay, Polly's timeouts) fires in the right
    /// order rather than all at once. Real elapsed time stays in the low milliseconds.
    /// </summary>
    private static async Task AdvanceUntilSettled(FakeTimeProvider clock, Task pending, TimeSpan budget)
    {
        var advanced = TimeSpan.Zero;
        while (!pending.IsCompleted && advanced < budget)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            advanced += TimeSpan.FromSeconds(1);
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task TwoSequentialRequests_BothReachTheTransport_AtTheDefaultInterval()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport();
        using var provider = BuildContainer(clock, transport);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        var first = await client.GetAsync("airports/SOF");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The second call must wait out MinRequestInterval inside the throttling handler, which
        // sits inside the resilience handler's per-attempt timeout. Before the fix that wait was
        // longer than the timeout enclosing it, and this request threw
        // Polly.Timeout.TimeoutRejectedException with the transport never having been called.
        var second = client.GetAsync("airports/IST");
        await AdvanceUntilSettled(clock, second, TimeSpan.FromMinutes(5));

        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task TwoSequentialRequests_BothReachTheTransport_WhenTheIntervalIsTunedUpwards()
    {
        // The regression guard for the *derivation*. A 45s interval is far beyond anything a fixed
        // timeout could absorb: under the library's defaults (10s per attempt, 30s in total) the
        // second request cannot possibly survive its wait, no matter how the retry or the throttle
        // behave. It passes only because AttemptTimeout and TotalRequestTimeout are computed from
        // MinRequestInterval — so re-hardcoding them, or reverting to the defaults, fails here.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport();
        using var provider = BuildContainer(clock, transport, TimeSpan.FromSeconds(45));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        await client.GetAsync("airports/SOF");

        var second = client.GetAsync("airports/IST");
        await AdvanceUntilSettled(clock, second, TimeSpan.FromMinutes(10));

        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task ARetriedRequest_IsMeteredOncePerAttempt()
    {
        // FlightAware bills per query, and a retry is another query. The usage handler is
        // registered INSIDE the resilience handler precisely so each attempt writes its own row —
        // metering the logical request instead would under-report spend by up to MaxRetryAttempts×,
        // and nothing outside the composed pipeline can tell the two apart.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport(HttpStatusCode.InternalServerError);
        var recorder = new CountingUsageRecorder();
        using var provider = BuildContainer(clock, transport, usageRecorder: recorder);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        var pending = client.GetAsync("airports/SOF");
        await AdvanceUntilSettled(clock, pending, TimeSpan.FromMinutes(5));

        Assert.Equal(HttpStatusCode.InternalServerError, (await pending).StatusCode);
        Assert.Equal(3, transport.Calls); // the initial attempt plus MaxRetryAttempts (2) retries
        Assert.Equal(3, recorder.Calls.Count);
        Assert.All(recorder.Calls, call =>
        {
            Assert.Equal(AeroApiCallOutcome.Failed, call.Outcome);
            Assert.Equal(500, call.StatusCode);
            Assert.Equal(AeroApiEndpointTemplate.Airports, call.EndpointTemplate);
        });
    }

    [Fact]
    public async Task ARequestAbandonedDuringItsThrottleWait_IsNotMetered()
    {
        // The mirror image: quota is spent by sending, not by queueing. A caller that gives up
        // while waiting on the gate consumed nothing, so it must leave no row — the same reasoning
        // that makes AeroApiThrottleGate release an abandoned reservation. Metering outside the
        // throttle would bill waits that never became requests.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport();
        var recorder = new CountingUsageRecorder();
        using var provider = BuildContainer(clock, transport, usageRecorder: recorder);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        await client.GetAsync("airports/SOF");
        Assert.Single(recorder.Calls);

        using var abandonment = new CancellationTokenSource();
        var abandoned = client.GetAsync("airports/IST", abandonment.Token);

        // Let it work its way down to the gate. The 10ms is a scheduling yield, not a simulated
        // wait — the interval it parks on runs on the fake clock, which is advanced nowhere near
        // the 12s the first request reserved, so nothing can release it while we set up.
        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(10);
        Assert.False(abandoned.IsCompleted);

        await abandonment.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        Assert.Equal(1, transport.Calls);
        Assert.Single(recorder.Calls); // still just the first request's row
    }

    [Fact]
    public async Task AHangingMeteringWrite_IsAbandoned_AndCostsNoExtraAeroApiCall()
    {
        // The meter is awaited in-band, innermost, so a stalled database spends the caller's
        // attempt budget. Left unbounded it would blow Polly's AttemptTimeout for a request whose
        // 200 had already arrived, and Polly would retry it — the meter that exists to control
        // spend causing up to MaxRetryAttempts× the spend, plus a 502 on a lookup that succeeded.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport();

        // Deliberately not `using`, unlike every other test here. If this guard ever catches a
        // regression, the metering write is still outstanding when the assertions below fail — and
        // disposing the container while one is outstanding blocks: measured, the assertion throws
        // in ~90ms and the runner then never reports it, turning a named failing test into a job
        // that hangs until CI kills it. It is disposed on the success path instead, where the write
        // has already settled; a container leaked by an already-failing run costs nothing.
        var provider = BuildContainer(clock, transport, usageRecorder: new HangingUsageRecorder());
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        var pending = client.GetAsync("airports/SOF");

        // The response is already in hand, yet the request is not done: the write is genuinely
        // awaited rather than fired and forgotten (which would dispose its scope out from under it).
        await Task.Delay(10);
        Assert.Equal(1, transport.Calls);
        Assert.False(pending.IsCompleted);

        // Ten simulated seconds: comfortably past the 5s the write is allowed, and nowhere near
        // the 42s AttemptTimeout — which is the point. If metering finishes inside its own budget,
        // the attempt is never even close to being at risk. Advancing as far as the attempt
        // timeout would instead be advancing into the very failure this bound exists to prevent.
        await AdvanceUntilSettled(clock, pending, TimeSpan.FromSeconds(10));

        // Asserted before awaiting: an unbounded write never completes, and this must fail on the
        // spot rather than hang the suite on a task that can never finish.
        Assert.True(pending.IsCompleted, "the metering write must not hold the request past its own budget");
        Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
        Assert.Equal(1, transport.Calls); // abandoned the row, not the attempt: no retry, no extra spend

        provider.Dispose(); // reached only when nothing is left in flight — see the note above
    }
}
