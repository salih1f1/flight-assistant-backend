using System.Diagnostics;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// #509: no test in this suite reaches AeroAPI over the network, and the refusal is immediate.
///
/// <para>
/// Three service-level stubs previously guarded three paths to this one client —
/// <c>IFlightSearchProvider</c> (#444), <c>IReferenceDataProvisioner</c> (#465) and the
/// verification sources' own clients — and each was added only after a test had already tripped
/// over the missing one, waiting out a real doomed call. This guards the client itself, so the next
/// service to reach AeroAPI cannot reintroduce that.
/// </para>
/// </summary>
[Collection("Integration")]
public class AeroApiCallBackstopTests(FlightAssistantWebApplicationFactory factory)
{
    /// <summary>
    /// Generous enough not to flake on a loaded CI runner, and far below the 12 s a single throttle
    /// wait costs. Measured values on this machine: 2 ms and 0 ms with the pipeline cleared, against
    /// 18 ms and 12,002 ms with it in place.
    /// </summary>
    private static readonly TimeSpan RefusalBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Two calls, not one, and that is the entire point of this test.
    ///
    /// <para>
    /// A throwing primary handler alone is enough for the <b>first</b> AeroAPI call a process makes:
    /// the throw is an <see cref="InvalidOperationException"/>, which
    /// <c>HttpClientResiliencePredicates.IsTransient</c> does not classify as transient, so it is
    /// never retried — and <c>AeroApiThrottleGate</c> hands out the first slot immediately. Measured:
    /// 18 ms with the whole pipeline in place. So a single-call test passes either way and proves
    /// nothing.
    /// </para>
    ///
    /// <para>
    /// The cost lands on every call <em>after</em> that, which waits a full
    /// <c>AeroApi:MinRequestInterval</c> (12 s by default) at the throttle before reaching the
    /// transport — measured at 12,002 ms. That is what a suite pays once more than one test touches
    /// AeroAPI, and it is what clearing the client's handler-builder actions removes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryOutboundAeroApiCall_IsRefusedAtOnce_IncludingTheSecond()
    {
        // Force the host to build so the factory's service overrides are applied.
        _ = factory.CreateClient();

        var http = factory.Services
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(AeroApiVerificationSource.HttpClientName);

        var first = await RefuseAndTime(http, "flights/BAW123");
        var second = await RefuseAndTime(http, "flights/BAW456");

        Assert.True(
            second < RefusalBudget,
            $"The second refusal took {second} (first: {first}), over the {RefusalBudget} budget. "
            + "That is the signature of AeroApiThrottleGate still being in the chain — the client's "
            + "HttpMessageHandlerBuilderActions are no longer being cleared, so every call after the "
            + "first waits a whole AeroApi:MinRequestInterval before being refused.");

        Assert.True(first < RefusalBudget, $"Even the first refusal took {first}.");
    }

    private static async Task<TimeSpan> RefuseAndTime(HttpClient http, string path)
    {
        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(path));
        stopwatch.Stop();

        // The refusal may arrive wrapped, so assert on the chain rather than the outermost type.
        Assert.Contains(
            FlightAssistantWebApplicationFactory.AeroApiCallRefused,
            Unwrap(exception).Select(e => e.Message).Aggregate(string.Empty, (a, b) => a + b));

        return stopwatch.Elapsed;
    }

    /// <summary>
    /// The client is still usable as a client — the backstop replaces its transport, it does not
    /// break its construction. A test that could not even resolve it would pass the check above for
    /// the wrong reason.
    /// </summary>
    [Fact]
    public void TheClientStillResolvesWithItsConfiguredBaseAddress()
    {
        _ = factory.CreateClient();

        var http = factory.Services
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(AeroApiVerificationSource.HttpClientName);

        Assert.NotNull(http.BaseAddress);
        Assert.Contains("aeroapi", http.BaseAddress!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            yield return current;
    }
}
