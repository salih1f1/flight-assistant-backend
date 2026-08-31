using System.Net;
using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Covers what <see cref="AeroApiUsageRecordingHandler"/> writes for a single request: the outcome
/// mapping, the low-cardinality endpoint template, and the promise that a broken meter never
/// breaks the flight request it was measuring. What it cannot cover is <i>where</i> the handler
/// sits in the composed client — that lives in <see cref="AeroApiClientPipelineTests"/>.
/// </summary>
public class AeroApiUsageRecordingHandlerTests
{
    /// <summary>Answers with a fixed status, or throws — optionally burning simulated time first.</summary>
    private sealed class StubTransport : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Exception? _throws;
        private readonly FakeTimeProvider? _clock;
        private readonly TimeSpan _timeSpentSending;

        public StubTransport(
            HttpStatusCode status = HttpStatusCode.OK,
            Exception? throws = null,
            FakeTimeProvider? clock = null,
            TimeSpan timeSpentSending = default)
        {
            _status = status;
            _throws = throws;
            _clock = clock;
            _timeSpentSending = timeSpentSending;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _clock?.Advance(_timeSpentSending);

            if (_throws is not null) throw _throws;

            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    private sealed class RecordingUsageRecorder : IAeroApiUsageRecorder
    {
        public List<AeroApiCall> Calls { get; } = [];

        public Task RecordAsync(AeroApiCall call, CancellationToken ct)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingUsageRecorder : IAeroApiUsageRecorder
    {
        public Task RecordAsync(AeroApiCall call, CancellationToken ct) =>
            throw new InvalidOperationException("no database in this container");
    }

    private static ServiceProvider BuildProvider(IAeroApiUsageRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        return services.BuildServiceProvider();
    }

    private static HttpClient BuildClient(
        ServiceProvider provider, HttpMessageHandler transport, TimeProvider clock)
    {
        var handler = new AeroApiUsageRecordingHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            NullLogger<AeroApiUsageRecordingHandler>.Instance)
        {
            InnerHandler = transport
        };

        // The same base address the real client is configured with, so the "/aeroapi/" root
        // segment these paths sit under is exercised rather than assumed away.
        return new HttpClient(handler) { BaseAddress = new Uri("https://aeroapi.flightaware.com/aeroapi/") };
    }

    [Fact]
    public async Task ASuccessfulRequest_RecordsOneSuccessRow()
    {
        var now = new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var recorder = new RecordingUsageRecorder();
        using var provider = BuildProvider(recorder);
        using var client = BuildClient(provider, new StubTransport(), clock);

        var response = await client.GetAsync("flights/AAL123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(AeroApiCallOutcome.Success, call.Outcome);
        Assert.Equal(200, call.StatusCode);
        Assert.Equal("GET", call.Method);
        Assert.Equal(AeroApiEndpointTemplate.Flights, call.EndpointTemplate);
        Assert.Equal("status", call.Caller);
        Assert.Equal(now.UtcDateTime, call.CalledUtc);
        Assert.Equal(DateTimeKind.Utc, call.CalledUtc.Kind);
    }

    [Fact]
    public async Task ThrottledByAeroApi_RecordsThrottled()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var recorder = new RecordingUsageRecorder();
        using var provider = BuildProvider(recorder);
        using var client = BuildClient(provider, new StubTransport(HttpStatusCode.TooManyRequests), clock);

        await client.GetAsync("schedules/2026-09-01/2026-09-02?origin=SOF");

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(AeroApiCallOutcome.Throttled, call.Outcome);
        Assert.Equal(429, call.StatusCode);
    }

    [Fact]
    public async Task AServerError_RecordsFailedWithItsStatus()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var recorder = new RecordingUsageRecorder();
        using var provider = BuildProvider(recorder);
        using var client = BuildClient(provider, new StubTransport(HttpStatusCode.InternalServerError), clock);

        await client.GetAsync("airports/SOF");

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(AeroApiCallOutcome.Failed, call.Outcome);
        Assert.Equal(500, call.StatusCode);
    }

    [Fact]
    public async Task ATransportFailure_RecordsFailedWithNoStatus_AndStillPropagates()
    {
        // The request left the process and is billed even though no response came back, so it must
        // still be metered — and the caller must still see the failure.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var recorder = new RecordingUsageRecorder();
        using var provider = BuildProvider(recorder);
        using var client = BuildClient(
            provider, new StubTransport(throws: new HttpRequestException("connection reset")), clock);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("flights/AAL123"));

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(AeroApiCallOutcome.Failed, call.Outcome);
        Assert.Null(call.StatusCode);
    }

    [Fact]
    public async Task AFailingRecorder_DoesNotFailTheRequest()
    {
        // Global Constraint 7: metering is best-effort. A container with no database at all (which
        // is exactly what AeroApiClientPipelineTests builds) must not turn every flight lookup into
        // a 500.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var provider = BuildProvider(new ThrowingUsageRecorder());
        using var client = BuildClient(provider, new StubTransport(), clock);

        var response = await client.GetAsync("flights/AAL123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("flights/AAL123?start=2026-09-01", AeroApiEndpointTemplate.Flights, "status")]
    [InlineData("schedules/2026-09-01/2026-09-02?origin=SOF&destination=IST",
        AeroApiEndpointTemplate.Schedules, "search")]
    [InlineData("airports/SOF", AeroApiEndpointTemplate.Airports, "timezone-lookup")]
    [InlineData("operators/AAL", "/operators/{...}", "unknown")]
    // An unrecognised path whose leading segment does not even read as an endpoint name is treated
    // as data and dropped whole, rather than stored on the off-chance it was a family name.
    [InlineData("AAL123/track", AeroApiEndpointTemplate.Unrecognised, "unknown")]
    public async Task RequestPaths_AreRecordedAsTemplates_NeverAsTheRawPath(
        string path, string expectedTemplate, string expectedCaller)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var recorder = new RecordingUsageRecorder();
        using var provider = BuildProvider(recorder);
        using var client = BuildClient(provider, new StubTransport(), clock);

        await client.GetAsync(path);

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(expectedTemplate, call.EndpointTemplate);
        Assert.Equal(expectedCaller, call.Caller);

        // Global Constraint 3: idents, dates and query strings are request data. None of it may
        // reach a usage row, where it would both explode cardinality and surface in the admin view.
        Assert.DoesNotContain("AAL", call.EndpointTemplate);
        Assert.DoesNotContain("2026", call.EndpointTemplate);
        Assert.DoesNotContain("SOF", call.EndpointTemplate);
        Assert.DoesNotContain("?", call.EndpointTemplate);
    }

    [Fact]
    public async Task AnAbsurdlyLongFamilySegment_FallsBackToUnrecognised_RatherThanOverflowingTheColumn()
    {
        // AeroApiCall.EndpointTemplate is varchar(128). A letters-only path segment longer than that
        // passes the "reads like an endpoint name" guard, and the row would then throw at INSERT,
        // be swallowed by the handler's best-effort catch, and vanish — losing a billed call from
        // the ledger silently. Unreachable today (all three call sites hit a named template), so
        // this pins the guard rather than a live behaviour.
        var overlongFamily = new string('a', 200);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var recorder = new RecordingUsageRecorder();
        using var provider = BuildProvider(recorder);
        using var client = BuildClient(provider, new StubTransport(), clock);

        await client.GetAsync($"{overlongFamily}/AAL123");

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(AeroApiEndpointTemplate.Unrecognised, call.EndpointTemplate);
        Assert.True(call.EndpointTemplate.Length <= 128);
    }

    [Fact]
    public async Task AFamilySegmentAtTheLengthBound_IsStillKept()
    {
        // The bound must not be so tight that it swallows plausible endpoint names — the guard is
        // there for data masquerading as a family name, not for long-ish real ones.
        var family = new string('a', 32);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var recorder = new RecordingUsageRecorder();
        using var provider = BuildProvider(recorder);
        using var client = BuildClient(provider, new StubTransport(), clock);

        await client.GetAsync($"{family}/AAL123");

        var call = Assert.Single(recorder.Calls);
        Assert.Equal($"/{family}/{{...}}", call.EndpointTemplate);
    }

    [Fact]
    public async Task TheRecordedDuration_IsTheTimeTheCallActuallyTook()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var recorder = new RecordingUsageRecorder();
        using var provider = BuildProvider(recorder);
        using var client = BuildClient(
            provider,
            new StubTransport(clock: clock, timeSpentSending: TimeSpan.FromMilliseconds(250)),
            clock);

        await client.GetAsync("airports/SOF");

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(250, call.DurationMs);
    }
}
