using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Builds the REAL container via <c>AddInfrastructure</c> — every other AeroAPI test constructs
/// <see cref="AeroApiThrottleGate"/> directly with a hand-supplied <see cref="TimeProvider"/>
/// (usually <c>FakeTimeProvider</c>), which bypasses DI entirely and would never have caught
/// <c>AeroApiThrottleGate</c>'s constructor requiring a <see cref="TimeProvider"/> that nothing in
/// the solution registers. That gap is silent in Task 1 (nothing resolves the gate yet) and
/// detonates the moment a real provider is added in Task 2/3 — these tests prove the graph is
/// actually buildable and stays that way.
/// </summary>
public class AeroApiDependencyInjectionTests
{
    private static ServiceProvider BuildContainer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AeroApi:ApiKey"] = "dummy-test-key"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        // isDevelopment: true so the unrelated Apns ValidateOnStart check (which AddInfrastructure
        // also registers) doesn't require Apns:* config this test has no reason to supply.
        services.AddInfrastructure(config, isDevelopment: true);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddInfrastructure_ResolvesAeroApiThrottleGate_FromTheRealContainer()
    {
        using var provider = BuildContainer();

        var gate = provider.GetRequiredService<AeroApiThrottleGate>();

        Assert.NotNull(gate);
    }

    [Fact]
    public void AddInfrastructure_ResolvesAeroApiThrottleGate_AsASingleton()
    {
        using var provider = BuildContainer();

        var first = provider.GetRequiredService<AeroApiThrottleGate>();
        var second = provider.GetRequiredService<AeroApiThrottleGate>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddInfrastructure_ResolvesAeroApiHttpClient_WithBaseAddressAndAuthHeaderConfigured()
    {
        using var provider = BuildContainer();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = factory.CreateClient("AeroApi");

        Assert.Equal(new Uri("https://aeroapi.flightaware.com/aeroapi/"), client.BaseAddress);
        Assert.True(client.DefaultRequestHeaders.Contains("x-apikey"));
        Assert.Equal("dummy-test-key", client.DefaultRequestHeaders.GetValues("x-apikey").Single());
    }

    [Fact]
    public void AddInfrastructure_ResolvesTimeProvider_ForTheAeroApiThrottleGateToConsume()
    {
        using var provider = BuildContainer();

        // The regression this whole file exists to catch: AeroApiThrottleGate's only constructor
        // requires a TimeProvider, and nothing but AddInfrastructure's own TryAddSingleton call
        // supplies one anywhere in this solution or in AddStandardResilienceHandler/the generic host.
        var timeProvider = provider.GetRequiredService<TimeProvider>();

        Assert.NotNull(timeProvider);
    }
}
