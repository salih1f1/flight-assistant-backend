using FlightAssistant.API.Telemetry;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using NSubstitute;

namespace FlightAssistant.API.Tests.Telemetry;

public class SanitizeAviationStackTelemetryProcessorTests
{
    [Fact]
    public void Process_DependencyTelemetryWithAccessKey_RedactsKeyAndCallsNext()
    {
        var next = Substitute.For<ITelemetryProcessor>();
        var sut = new SanitizeAviationStackTelemetryProcessor(next);
        var dependency = new DependencyTelemetry
        {
            Data = "https://api.aviationstack.com/v1/flights?access_key=SECRET123&flight_iata=BA123&flight_date=2026-06-13"
        };

        sut.Process(dependency);

        Assert.DoesNotContain("SECRET123", dependency.Data);
        Assert.Contains("flight_iata=BA123", dependency.Data);
        Assert.Contains("flight_date=2026-06-13", dependency.Data);
        next.Received(1).Process(dependency);
    }

    [Fact]
    public void Process_DependencyTelemetryWithoutAccessKey_LeavesDataUnchangedAndCallsNext()
    {
        var next = Substitute.For<ITelemetryProcessor>();
        var sut = new SanitizeAviationStackTelemetryProcessor(next);
        const string originalData = "https://aerodatabox.p.rapidapi.com/flights/BA123/2026-06-13";
        var dependency = new DependencyTelemetry
        {
            Data = originalData
        };

        sut.Process(dependency);

        Assert.Equal(originalData, dependency.Data);
        next.Received(1).Process(dependency);
    }

    [Fact]
    public void Process_NonDependencyTelemetry_PassesThroughUnchanged()
    {
        var next = Substitute.For<ITelemetryProcessor>();
        var sut = new SanitizeAviationStackTelemetryProcessor(next);
        var trace = new TraceTelemetry("access_key=SECRET123 should not be touched here");

        sut.Process(trace);

        Assert.Equal("access_key=SECRET123 should not be touched here", trace.Message);
        next.Received(1).Process(trace);
    }
}
