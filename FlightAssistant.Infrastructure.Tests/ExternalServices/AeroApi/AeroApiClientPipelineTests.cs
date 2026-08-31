using System.Net;
using System.Text;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private sealed class CountingTransport : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private static ServiceProvider BuildContainer(
        FakeTimeProvider clock, CountingTransport transport, TimeSpan? minRequestInterval = null)
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
}
