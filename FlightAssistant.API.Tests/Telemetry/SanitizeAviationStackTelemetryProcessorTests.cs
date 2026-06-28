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
        const string originalData = "http://api.aviationstack.com/v1/flights?flight_iata=BA123";
        var dependency = new DependencyTelemetry
        {
            Data = originalData
        };

        sut.Process(dependency);

        Assert.Equal(originalData, dependency.Data);
        next.Received(1).Process(dependency);
    }

    [Fact]
    public void Process_TraceTelemetryWithAccessKeyInMessage_RedactsKeyAndCallsNext()
    {
        var next = Substitute.For<ITelemetryProcessor>();
        var sut = new SanitizeAviationStackTelemetryProcessor(next);
        var trace = new TraceTelemetry("Request failed for https://api.aviationstack.com/v1/flights?access_key=SECRET123&flight_iata=BA123");

        sut.Process(trace);

        Assert.DoesNotContain("SECRET123", trace.Message);
        Assert.Contains("access_key=***", trace.Message);
        Assert.Contains("flight_iata=BA123", trace.Message);
        next.Received(1).Process(trace);
    }

    [Fact]
    public void Process_TraceTelemetryWithAccessKeyInProperties_RedactsValueAndCallsNext()
    {
        var next = Substitute.For<ITelemetryProcessor>();
        var sut = new SanitizeAviationStackTelemetryProcessor(next);
        var trace = new TraceTelemetry("Resilience event occurred.");
        trace.Properties["Result"] = "StatusCode: 200, Request Message: https://api.aviationstack.com/v1/flights?access_key=SECRET123&flight_iata=BA123";

        sut.Process(trace);

        Assert.DoesNotContain("SECRET123", trace.Properties["Result"]);
        Assert.Contains("access_key=***", trace.Properties["Result"]);
        Assert.Contains("flight_iata=BA123", trace.Properties["Result"]);
        next.Received(1).Process(trace);
    }

    [Fact]
    public void Process_ExceptionTelemetryWithAccessKeyInProperties_RedactsValueAndCallsNext()
    {
        var next = Substitute.For<ITelemetryProcessor>();
        var sut = new SanitizeAviationStackTelemetryProcessor(next);
        var exceptionTelemetry = new ExceptionTelemetry(new InvalidOperationException("boom"));
        exceptionTelemetry.Properties["Result"] = "Request Message: https://api.aviationstack.com/v1/flights?access_key=SECRET123&flight_date=2026-06-13";

        sut.Process(exceptionTelemetry);

        Assert.DoesNotContain("SECRET123", exceptionTelemetry.Properties["Result"]);
        Assert.Contains("access_key=***", exceptionTelemetry.Properties["Result"]);
        Assert.Contains("flight_date=2026-06-13", exceptionTelemetry.Properties["Result"]);
        next.Received(1).Process(exceptionTelemetry);
    }

    [Fact]
    public void Process_TraceTelemetryWithoutAccessKeyAnywhere_LeavesMessageAndPropertiesUnchangedAndCallsNext()
    {
        var next = Substitute.For<ITelemetryProcessor>();
        var sut = new SanitizeAviationStackTelemetryProcessor(next);
        const string originalMessage = "Resilience event occurred for AviationStackFlightStatusProvider";
        var trace = new TraceTelemetry(originalMessage);
        trace.Properties["Result"] = "StatusCode: 200, RequestUri: http://api.aviationstack.com/v1/flights?flight_iata=BA123";

        sut.Process(trace);

        Assert.Equal(originalMessage, trace.Message);
        Assert.Equal("StatusCode: 200, RequestUri: http://api.aviationstack.com/v1/flights?flight_iata=BA123", trace.Properties["Result"]);
        next.Received(1).Process(trace);
    }
}
