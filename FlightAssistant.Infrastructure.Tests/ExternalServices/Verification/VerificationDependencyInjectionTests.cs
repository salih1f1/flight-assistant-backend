using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Enums;
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
/// dataset survives the job's per-cycle scope), neither client carries credentials, resolving the
/// registry alone never talks to the network, and each source actually uses its own named client
/// (fix round 1, items 4-5).
/// </summary>
public class VerificationDependencyInjectionTests
{
    /// <summary>One row: <c>iata_code=LHR</c>. Enough for a swapped handler to answer a real lookup
    /// successfully without tripping the "zero usable rows" source-error check (fix round 1, item 6).</summary>
    private const string MinimalOurAirportsCsv =
        "id,ident,type,name,latitude_deg,longitude_deg,elevation_ft,continent,country_name,iso_country,region_name,iso_region,local_region,municipality,scheduled_service,gps_code,icao_code,iata_code,local_code,home_link,wikipedia_link,keywords,score,last_updated\n" +
        "1,EGLL,large_airport,\"Heathrow\",51.47,-0.46,83,,,GB,,,,\"London\",yes,EGLL,EGLL,LHR,,,,,900,2024-01-01\n";

    /// <summary>One record: <c>cca2=GB</c>, with both required fields present.</summary>
    private const string MinimalMledozeJson =
        """[{"cca2":"GB","cca3":"GBR","ccn3":"826","region":"Europe","subregion":"Northern Europe","name":{"common":"United Kingdom"}}]""";

    private sealed class RecordingHandler(string body, string contentType) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }

    /// <summary>Throws rather than ever reaching a real transport -- used both for "this client
    /// should never be touched by this test" and, registered on the default/unnamed client name, as
    /// a safety net against a client-name bug silently making a real network call (fix round 2,
    /// item 1) instead of failing the test outright.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected request to {request.RequestUri} — this handler should never be reached.");
    }

    private static ServiceProvider BuildContainer(Action<IServiceCollection>? configure = null)
    {
        var settings = new Dictionary<string, string?> { ["AeroApi:ApiKey"] = "dummy-test-key" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddAppCaching(config);
        services.AddInfrastructure(config, isDevelopment: true);
        configure?.Invoke(services);

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

    /// <summary>Fix round 1, item 4: nothing about resolving the registry (or the sources behind
    /// it) may reach the network -- a source loads its dataset lazily, only from a real
    /// <c>LookupAsync</c> call.</summary>
    [Fact]
    public void ResolvingTheRegistry_MakesNoHttpRequest()
    {
        var ourAirports = new RecordingHandler(MinimalOurAirportsCsv, "text/csv");
        var mledoze = new RecordingHandler(MinimalMledozeJson, "application/json");

        using var provider = BuildContainer(services =>
        {
            services.AddHttpClient(OurAirportsVerificationSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ourAirports);
            services.AddHttpClient(MledozeCountriesVerificationSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => mledoze);
        });

        var registry = provider.GetRequiredService<IVerificationSourceRegistry>();
        _ = registry.TryGet("ourairports");
        _ = registry.TryGet("mledoze");
        _ = registry.Sources;

        Assert.Empty(ourAirports.Requests);
        Assert.Empty(mledoze.Requests);
    }

    /// <summary>
    /// Fix round 1, item 5 (completed in fix round 2, item 1): proves the OurAirports source
    /// actually calls <c>IHttpClientFactory.CreateClient(OurAirportsVerificationSource.HttpClientName)</c>
    /// rather than some other client. Unlike round 1's version, this also swaps in a
    /// <see cref="ThrowingHandler"/> on the <b>default/unnamed</b> client name (<c>string.Empty</c>)
    /// -- so a client-name bug (e.g. accidentally requesting the default client) fails this test
    /// outright instead of silently making a real network call to ourairports.com, which round 1's
    /// version would have done (it only swapped the two named clients, leaving the default one at
    /// its real <see cref="SocketsHttpHandler"/>).
    /// </summary>
    [Fact]
    public async Task TheOurAirportsSourceFromTheContainer_DownloadsThroughItsOwnNamedClient_NeverTheDefault()
    {
        var ourAirports = new RecordingHandler(MinimalOurAirportsCsv, "text/csv");
        var mledoze = new ThrowingHandler(); // never expected to be touched by this test
        var defaultClient = new ThrowingHandler(); // ditto -- the unnamed/default client

        using var provider = BuildContainer(services =>
        {
            services.AddHttpClient(OurAirportsVerificationSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ourAirports);
            services.AddHttpClient(MledozeCountriesVerificationSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => mledoze);
            services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => defaultClient);
        });

        var source = provider.GetRequiredService<IVerificationSourceRegistry>().TryGet("ourairports")!;
        var result = await source.LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        Assert.False(result.IsError);
        Assert.True(result.Lookup!.IsFound);
        Assert.Single(ourAirports.Requests);
    }

    /// <summary>Fix round 2, item 1: the mledoze equivalent of the test above -- round 1 had no
    /// DI-level proof for mledoze at all, and the unit-level <c>FakeHttpClientFactory</c> (see
    /// <c>MledozeCountriesVerificationSourceTests</c>) ignored the requested client name, so
    /// pointing mledoze at the wrong named client (or the default one) failed nothing.</summary>
    [Fact]
    public async Task TheMledozeSourceFromTheContainer_DownloadsThroughItsOwnNamedClient_NeverTheDefault()
    {
        var ourAirports = new ThrowingHandler(); // never expected to be touched by this test
        var mledoze = new RecordingHandler(MinimalMledozeJson, "application/json");
        var defaultClient = new ThrowingHandler(); // the unnamed/default client

        using var provider = BuildContainer(services =>
        {
            services.AddHttpClient(OurAirportsVerificationSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ourAirports);
            services.AddHttpClient(MledozeCountriesVerificationSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => mledoze);
            services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => defaultClient);
        });

        var source = provider.GetRequiredService<IVerificationSourceRegistry>().TryGet("mledoze")!;
        var result = await source.LookupAsync(VerificationEntityType.Country, VerificationSourceLookupKey.ForCountry("GB"));

        Assert.False(result.IsError);
        Assert.True(result.Lookup!.IsFound);
        Assert.Single(mledoze.Requests);
    }
}
