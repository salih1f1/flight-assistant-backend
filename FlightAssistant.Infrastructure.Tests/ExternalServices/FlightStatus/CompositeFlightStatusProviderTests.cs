using FlightAssistant.Infrastructure.ExternalServices.FlightStatus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightStatus;

/// <summary>
/// Tests for <see cref="CompositeFlightStatusProvider"/>'s primary/fallback selection and
/// <see cref="CompositeFlightStatusProvider.ProviderName"/> tracking.
///
/// Both <see cref="AeroDataBoxFlightStatusProvider"/> and <see cref="AviationStackFlightStatusProvider"/>
/// are wired up with <see cref="HttpClient"/> instances pointed at an unreachable local port
/// (127.0.0.1:1, nothing listening), so any HTTP call fails fast with a connection-refused
/// <see cref="HttpRequestException"/> without touching the network or DNS.
/// </summary>
public class CompositeFlightStatusProviderTests
{
    private static (AeroDataBoxFlightStatusProvider primary, AviationStackFlightStatusProvider fallback) CreateProviders()
    {
        var unreachable = new Uri("http://127.0.0.1:1/");

        var primaryHttp = new HttpClient { BaseAddress = unreachable, Timeout = TimeSpan.FromSeconds(5) };
        var primary = new AeroDataBoxFlightStatusProvider(primaryHttp);

        var fallbackHttp = new HttpClient { BaseAddress = unreachable, Timeout = TimeSpan.FromSeconds(5) };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy-key" })
            .Build();
        var fallback = new AviationStackFlightStatusProvider(fallbackHttp, config);

        return (primary, fallback);
    }

    [Fact]
    public void ProviderName_InitiallyReflectsPrimaryProvider()
    {
        var (primary, fallback) = CreateProviders();
        var composite = new CompositeFlightStatusProvider(primary, fallback, NullLogger<CompositeFlightStatusProvider>.Instance);

        Assert.Equal("AeroDataBox", composite.ProviderName);
    }

    [Fact]
    public async Task GetStatusAsync_PrimaryThrows_FallsBackAndUpdatesProviderName()
    {
        var (primary, fallback) = CreateProviders();
        var composite = new CompositeFlightStatusProvider(primary, fallback, NullLogger<CompositeFlightStatusProvider>.Instance);

        // Primary's HttpClient points at an unreachable host, so GetStatusAsync throws
        // HttpRequestException internally. CompositeFlightStatusProvider catches it and
        // falls back to AviationStack, whose HttpClient is also unreachable and will
        // itself fail/return null - but ProviderName must be updated to the fallback's
        // name as soon as the fallback path is attempted.
        var result = await composite.GetStatusAsync("BA123", DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.Null(result);
        Assert.Equal("AviationStack", composite.ProviderName);
    }
}
