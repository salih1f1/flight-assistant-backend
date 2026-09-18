using FlightAssistant.Application;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.DataSources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.DataSources
{
    public class DataSourceCatalogTests
    {
        private static readonly DataSourceCatalog Catalog = new();

        /// <summary>
        /// Builds the real container the same way <c>AeroApiDependencyInjectionTests</c> and
        /// <c>GitHubDependencyInjectionTests</c> do, rather than a hand-rolled one -- the point of
        /// <see cref="EveryNamedHttpClientHasACatalogEntry"/> is to see what <c>AddInfrastructure</c>
        /// actually registers, so it has to be the real registration path. <c>isDevelopment: true</c>
        /// skips the unrelated Apns <c>ValidateOnStart</c> check, which needs config this test has no
        /// reason to supply and which does not run until something actually starts the host anyway.
        /// </summary>
        private static ServiceProvider BuildContainer(Action<IServiceCollection>? configure = null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["AeroApi:ApiKey"] = "dummy-test-key",
                ["GitHub:Token"] = "github_pat_test_token",
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddAppCaching(config);
            services.AddInfrastructure(config, isDevelopment: true);
            services.AddApplication();
            configure?.Invoke(services);

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Every name <c>AddHttpClient</c> actually registered in the real container, read off the
        /// container itself rather than a second hand-maintained list. <c>IHttpClientFactory</c>
        /// keeps no public "list of names" API, but every <c>AddHttpClient(name, ...)</c> overload
        /// this codebase uses -- a client-only delegate, a service-provider-and-client delegate, or a
        /// builder method like <c>ConfigurePrimaryHttpMessageHandler</c> -- ends up registering an
        /// <see cref="IConfigureOptions{T}"/> of <see cref="HttpClientFactoryOptions"/> that, once
        /// resolved, is a <see cref="ConfigureNamedOptions{T}"/> carrying that name. Resolving
        /// (rather than inspecting <see cref="ServiceDescriptor.ImplementationInstance"/> directly)
        /// matters: the service-provider-and-client overload -- what AeroAPI's own registration uses
        /// -- registers an <see cref="ServiceDescriptor.ImplementationFactory"/>, not an instance, so
        /// a descriptor-only scan would silently miss it.
        /// </summary>
        private static IReadOnlyList<string> RegisteredHttpClientNames()
        {
            using var provider = BuildContainer();
            return provider.GetServices<IConfigureOptions<HttpClientFactoryOptions>>()
                .OfType<ConfigureNamedOptions<HttpClientFactoryOptions>>()
                .Select(o => o.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.Ordinal)
                .ToList()!;
        }

        [Fact]
        public void EveryKeyIsUnique()
        {
            var keys = Catalog.Sources.Select(s => s.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void EverySourceExplainsWhatIsChecked()
        {
            Assert.All(Catalog.Sources, s => Assert.False(string.IsNullOrWhiteSpace(s.WhatIsChecked)));
        }

        /// <summary>
        /// The test that earns its keep. Six sources were invisible on the health page because
        /// nothing forced a new integration to appear there. This builds the REAL container
        /// (<c>AddInfrastructure</c>) and reads the HttpClient names it actually registered -- not a
        /// second hand-maintained list, which would only ever catch "someone edited one list but not
        /// the other" and stay silent on the realistic failure: a new <c>AddHttpClient</c> call added
        /// to <c>DependencyInjection.cs</c> with no catalog entry and no list updated anywhere. Proven
        /// by adding <c>services.AddHttpClient("TotallyNewProbe", c => { })</c> to
        /// <c>DependencyInjection.cs</c> directly: this test fails, naming <c>TotallyNewProbe</c>.
        /// </summary>
        [Fact]
        public void EveryNamedHttpClientHasACatalogEntry()
        {
            var registered = RegisteredHttpClientNames();
            var catalogued = Catalog.Sources
                .Where(s => s.HttpClientName is not null)
                .Select(s => s.HttpClientName!)
                .ToHashSet(StringComparer.Ordinal);

            var missing = registered.Where(name => !catalogued.Contains(name)).ToList();

            Assert.True(
                missing.Count == 0,
                $"These named HttpClients have no DataSourceCatalog entry, so they would be " +
                $"invisible on the health page: {string.Join(", ", missing)}");
        }

        /// <summary>
        /// AeroDataBox is the one source whose every real call is billed, so its probe checks
        /// reachability only and must say so. AeroAPI's probe reads the account-usage report, which
        /// FlightAware bills at $0.00 -- it must NOT carry a cost warning, or operators will avoid a
        /// free check.
        /// </summary>
        [Fact]
        public void TheMeteredSourceCarriesACostNote_AndTheFreeOneDoesNot()
        {
            Assert.NotNull(Catalog.TryGet(DataSourceKeys.AeroDataBox)!.ProbeCostNote);
            Assert.Null(Catalog.TryGet(DataSourceKeys.AeroApi)!.ProbeCostNote);
        }

        [Fact]
        public void TryGet_ReturnsNull_ForAnUnknownKey()
        {
            Assert.Null(Catalog.TryGet("not-a-source"));
        }
    }
}
