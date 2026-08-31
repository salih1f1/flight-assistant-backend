using System.Net;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Proves the DI intent behind <see cref="AeroApiThrottleGate"/> being a singleton: two distinct
/// <see cref="AeroApiThrottlingHandler"/> instances (standing in for two different named
/// <see cref="HttpClient"/>s) that share one gate must still be spaced by
/// <see cref="AeroApiOptions.MinRequestInterval"/> across both of them, not per handler.
/// </summary>
public class AeroApiThrottlingHandlerTests
{
    private sealed class RecordingInnerHandler(TimeProvider clock, List<DateTimeOffset> sendTimestamps) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sendTimestamps.Add(clock.GetUtcNow());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static IOptionsMonitor<AeroApiOptions> OptionsMonitor(TimeSpan minInterval)
    {
        var monitor = Substitute.For<IOptionsMonitor<AeroApiOptions>>();
        monitor.CurrentValue.Returns(new AeroApiOptions { ApiKey = "dummy", MinRequestInterval = minInterval });
        return monitor;
    }

    [Fact]
    public async Task TwoHandlersSharingOneGate_SpaceRequestsAcrossBothClients()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AeroApiThrottleGate(clock);
        var options = OptionsMonitor(TimeSpan.FromSeconds(12));
        var sendTimestamps = new List<DateTimeOffset>();

        // Two handlers, standing in for two separately-named HttpClients, deliberately share the
        // one AeroApiThrottleGate instance the way DI wires a singleton gate under every client.
        var handlerA = new AeroApiThrottlingHandler(gate, options)
        {
            InnerHandler = new RecordingInnerHandler(clock, sendTimestamps)
        };
        var handlerB = new AeroApiThrottlingHandler(gate, options)
        {
            InnerHandler = new RecordingInnerHandler(clock, sendTimestamps)
        };

        using var clientA = new HttpClient(handlerA) { BaseAddress = new Uri("http://stub-a/") };
        using var clientB = new HttpClient(handlerB) { BaseAddress = new Uri("http://stub-b/") };

        await clientA.GetAsync("/a"); // t=0, no wait: first-ever reservation on the gate

        var pendingSendFromClientB = clientB.GetAsync("/b");
        Assert.False(pendingSendFromClientB.IsCompleted); // client B queues behind client A's reservation

        clock.Advance(TimeSpan.FromSeconds(12));
        await pendingSendFromClientB;

        Assert.Equal(2, sendTimestamps.Count);
        Assert.True(sendTimestamps[1] - sendTimestamps[0] >= TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task TwoIndependentGates_DoNotThrottleEachOther()
    {
        // Sanity check for the singleton requirement itself: two handlers on two *different* gate
        // instances must NOT wait on one another — proving the shared-gate test above is actually
        // exercising shared state, not some other serialization.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = OptionsMonitor(TimeSpan.FromSeconds(12));
        var sendTimestamps = new List<DateTimeOffset>();

        var handlerA = new AeroApiThrottlingHandler(new AeroApiThrottleGate(clock), options)
        {
            InnerHandler = new RecordingInnerHandler(clock, sendTimestamps)
        };
        var handlerB = new AeroApiThrottlingHandler(new AeroApiThrottleGate(clock), options)
        {
            InnerHandler = new RecordingInnerHandler(clock, sendTimestamps)
        };

        using var clientA = new HttpClient(handlerA) { BaseAddress = new Uri("http://stub-a/") };
        using var clientB = new HttpClient(handlerB) { BaseAddress = new Uri("http://stub-b/") };

        await clientA.GetAsync("/a");
        await clientB.GetAsync("/b"); // completes immediately: its own gate has no prior reservation

        Assert.Equal(2, sendTimestamps.Count);
    }
}
