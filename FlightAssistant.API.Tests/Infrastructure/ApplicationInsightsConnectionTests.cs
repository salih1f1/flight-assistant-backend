using FlightAssistant.API.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace FlightAssistant.API.Tests.Infrastructure;

/// <summary>
/// <see cref="ApplicationInsightsConnection"/> (#440). App Service sets the environment variable
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>, which never maps to the documented
/// <c>ApplicationInsights:ConnectionString</c> key, so the resolver must fall back to it.
/// </summary>
public class ApplicationInsightsConnectionTests
{
    private const string Dummy = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:9/";
    private const string OtherDummy = "InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=http://127.0.0.1:9/";

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Resolve_OnlyDocumentedKeySet_ReturnsIt()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            [ApplicationInsightsConnection.ConfigurationKey] = Dummy,
        });

        Assert.Equal(Dummy, ApplicationInsightsConnection.Resolve(configuration));
    }

    [Fact]
    public void Resolve_OnlyPlatformKeySet_ReturnsIt()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            [ApplicationInsightsConnection.PlatformKey] = Dummy,
        });

        Assert.Equal(Dummy, ApplicationInsightsConnection.Resolve(configuration));
    }

    [Fact]
    public void Resolve_BothSetToDifferentValues_ReturnsTheDocumentedKeysValue()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            [ApplicationInsightsConnection.ConfigurationKey] = Dummy,
            [ApplicationInsightsConnection.PlatformKey] = OtherDummy,
        });

        Assert.Equal(Dummy, ApplicationInsightsConnection.Resolve(configuration));
    }

    [Fact]
    public void Resolve_DocumentedKeyIsWhitespaceAndPlatformKeyIsSet_ReturnsThePlatformKey()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            [ApplicationInsightsConnection.ConfigurationKey] = "   ",
            [ApplicationInsightsConnection.PlatformKey] = Dummy,
        });

        Assert.Equal(Dummy, ApplicationInsightsConnection.Resolve(configuration));
    }

    [Fact]
    public void Resolve_NeitherSet_ReturnsNull()
    {
        var configuration = Build(new Dictionary<string, string?>());

        Assert.Null(ApplicationInsightsConnection.Resolve(configuration));
    }

    [Fact]
    public void Resolve_BothWhitespace_ReturnsNull()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            [ApplicationInsightsConnection.ConfigurationKey] = "   ",
            [ApplicationInsightsConnection.PlatformKey] = "  ",
        });

        Assert.Null(ApplicationInsightsConnection.Resolve(configuration));
    }
}
