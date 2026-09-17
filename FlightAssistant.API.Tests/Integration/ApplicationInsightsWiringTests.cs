using FlightAssistant.API.Infrastructure;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// #440: App Service sets <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>, not the documented
/// <c>ApplicationInsights:ConnectionString</c> key. <see cref="ApplicationInsightsConnection"/>
/// must be wired into <c>Program.cs</c>'s <c>AddApplicationInsightsTelemetry()</c> guard for the
/// platform-only setting to actually enable the SDK.
/// </summary>
[Collection("Integration")]
public class ApplicationInsightsWiringTests(FlightAssistantWebApplicationFactory factory)
{
    // LiveEndpoint is pinned alongside IngestionEndpoint (#440 fix round 1, finding 5):
    // EnableQuickPulseMetricStream defaults to true, so resolving TelemetryConfiguration (as both
    // tests here do) initializes a Live Metrics module whose default target is a real Microsoft
    // endpoint unless LiveEndpoint is also redirected to loopback.
    private const string Dummy = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:9/;LiveEndpoint=http://127.0.0.1:9/";

    [Fact]
    public void PlatformKeyOnly_EnablesTheSdk()
    {
        using var host = factory.WithWebHostBuilder(b =>
            b.UseSetting(ApplicationInsightsConnection.PlatformKey, Dummy));

        var telemetryClient = host.Services.GetService<TelemetryClient>();

        Assert.NotNull(telemetryClient);

        var telemetryConfiguration = host.Services.GetService<TelemetryConfiguration>();
        Assert.NotNull(telemetryConfiguration);
        Assert.Equal(Dummy, telemetryConfiguration!.ConnectionString);
    }

    [Fact]
    public void NoKey_LeavesTheSdkOff()
    {
        var telemetryClient = factory.Services.GetService<TelemetryClient>();

        Assert.Null(telemetryClient);
    }
}
