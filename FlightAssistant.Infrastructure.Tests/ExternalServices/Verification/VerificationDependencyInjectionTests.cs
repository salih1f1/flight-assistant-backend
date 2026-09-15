using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// Builds the real container (<c>AddInfrastructure</c>) rather than constructing
/// <see cref="OurAirportsVerificationSource"/>/<see cref="MledozeCountriesVerificationSource"/> by
/// hand, the way <c>GitHubDependencyInjectionTests</c> does for the CI provider -- only this proves
/// what DI actually wires: both sources land in the registry, both are singletons (so a cached
/// dataset survives the job's per-cycle scope), and neither client carries credentials.
/// </summary>
public class VerificationDependencyInjectionTests
{
    private static ServiceProvider BuildContainer()
    {
        var settings = new Dictionary<string, string?> { ["AeroApi:ApiKey"] = "dummy-test-key" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddAppCaching(config);
        services.AddInfrastructure(config, isDevelopment: true);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheRegistry_ResolvesBothFreeSourcesByName()
    {
        using var provider = BuildContainer();
        var registry = provider.GetRequiredService<IVerificationSourceRegistry>();

        Assert.IsType<OurAirportsVerificationSource>(registry.TryGet("ourairports"));
        Assert.IsType<MledozeCountriesVerificationSource>(registry.TryGet("mledoze"));
        Assert.Equal(2, registry.Sources.Count);
    }

    [Fact]
    public void TheRegistry_ReturnsNull_ForAnUnregisteredSourceName()
    {
        using var provider = BuildContainer();
        var registry = provider.GetRequiredService<IVerificationSourceRegistry>();

        // aeroapi isn't registered as a verification source until #367 step 9.
        Assert.Null(registry.TryGet("aeroapi"));
    }

    [Fact]
    public void EachSource_IsASingleton_SoItsCachedDatasetSurvivesAcrossScopes()
    {
        using var provider = BuildContainer();

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var fromA = scopeA.ServiceProvider.GetRequiredService<IVerificationSourceRegistry>().TryGet("ourairports");
        var fromB = scopeB.ServiceProvider.GetRequiredService<IVerificationSourceRegistry>().TryGet("ourairports");

        Assert.Same(fromA, fromB);
    }

    [Theory]
    [InlineData(OurAirportsVerificationSource.HttpClientName)]
    [InlineData(MledozeCountriesVerificationSource.HttpClientName)]
    public void TheHttpClients_CarryNoCredentials(string clientName)
    {
        using var provider = BuildContainer();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("x-apikey"));
        Assert.NotEmpty(client.DefaultRequestHeaders.UserAgent);
    }

    [Fact]
    public void TheOurAirportsClient_AllowsMinutes_ForTheLargeDownload()
    {
        using var provider = BuildContainer();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(OurAirportsVerificationSource.HttpClientName);

        Assert.True(client.Timeout >= TimeSpan.FromMinutes(1));
    }
}
