using FlightAssistant.Application;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi.Caching;
using FluentValidation;
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
/// actually buildable and stays that way, now including the search provider Task 2 added (every
/// one of ITS tests also constructs it directly with hand-supplied fakes, same gap as Task 1's).
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

        // AddAppCaching is registered separately from AddInfrastructure in Program.cs (the real
        // composition root) — AeroApiAirportTimezoneLookup needs ICacheService, so it must be
        // registered here too, or resolving it (and anything that depends on it) would throw for
        // a reason that has nothing to do with the AeroAPI wiring under test.
        services.AddAppCaching(config);

        // isDevelopment: true so the unrelated Apns ValidateOnStart check (which AddInfrastructure
        // also registers) doesn't require Apns:* config this test has no reason to supply.
        services.AddInfrastructure(config, isDevelopment: true);

        // Program.cs calls AddApplication() and AddInfrastructure() together — needed here so
        // SearchFlightsQueryValidator, which now takes IFlightSearchHorizonOptions in its
        // constructor, can actually be resolved the way FluentValidation's DI-scanned validators
        // are resolved in production, not constructed directly with a hand-supplied fake.
        services.AddApplication();

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

    [Fact]
    public void AddInfrastructure_ResolvesIFlightSearchProvider_AsTheCachingDecorator()
    {
        using var provider = BuildContainer();

        // The regression this test exists to catch: issue #282 put a caching decorator in front of
        // AeroApiFlightSearchProvider precisely so GET /flights/search (anonymous, only rate-limited
        // per IP) stops going straight to the metered provider on every request. Every one of the
        // decorator's own tests constructs it directly with hand-supplied fakes, so none of them go
        // through DI — a wrong lifetime, a missing dependency, or the registration reverting to the
        // bare provider (silently re-opening the budget hole this decorator exists to close) would
        // be invisible until a real request hit it in production.
        var searchProvider = provider.GetRequiredService<IFlightSearchProvider>();

        Assert.IsType<AeroApiFlightSearchCachingDecorator>(searchProvider);
    }

    [Fact]
    public void AddInfrastructure_ResolvesAeroApiAirportTimezoneLookup_WithItsHttpClientAndCache()
    {
        using var provider = BuildContainer();

        // Resolving this directly (rather than only transitively through IFlightSearchProvider)
        // isolates which dependency is missing if this ever throws: ICacheService (from
        // AddAppCaching), IOptions<AeroApiOptions>, or the named "AeroApi" HttpClient.
        var lookup = provider.GetRequiredService<AeroApiAirportTimezoneLookup>();

        Assert.NotNull(lookup);
    }

    [Fact]
    public void AddInfrastructure_ResolvesIFlightStatusProvider_AsAeroApiFlightStatusProvider()
    {
        using var provider = BuildContainer();

        // The regression this test exists to catch: every one of AeroApiFlightStatusProviderTests'
        // own tests constructs
        // AeroApiFlightStatusProvider directly with a hand-supplied HttpClient/TimeProvider/logger
        // — none go through DI, so a wrong lifetime, a missing dependency (this constructor needs
        // TimeProvider, which nothing but AddInfrastructure's own TryAddSingleton call supplies),
        // or the registration being dropped entirely would be invisible until a real status sync
        // hit it in production.
        var statusProvider = provider.GetRequiredService<IFlightStatusProvider>();

        Assert.IsType<AeroApiFlightStatusProvider>(statusProvider);
    }

    [Fact]
    public void AddInfrastructure_ResolvesAeroApiFlightStatusProvider_AsAScopedInstancePerScope()
    {
        using var root = BuildContainer();

        using (var scopeA = root.CreateScope())
        {
            var first = scopeA.ServiceProvider.GetRequiredService<AeroApiFlightStatusProvider>();
            var second = scopeA.ServiceProvider.GetRequiredService<AeroApiFlightStatusProvider>();
            Assert.Same(first, second); // same within a scope

            using var scopeB = root.CreateScope();
            var third = scopeB.ServiceProvider.GetRequiredService<AeroApiFlightStatusProvider>();
            Assert.NotSame(first, third); // different across scopes — proves it's Scoped, not Singleton
        }
    }

    [Fact]
    public void AddInfrastructure_ResolvesIFlightStatusProvider_WithAeroApiProviderName()
    {
        using var provider = BuildContainer();

        var statusProvider = provider.GetRequiredService<IFlightStatusProvider>();

        Assert.Equal("AeroAPI", statusProvider.ProviderName);
    }

    [Fact]
    public void AddInfrastructure_ResolvesIFlightSearchHorizonOptions_FromAeroApiOptions()
    {
        using var provider = BuildContainer();

        // SearchFlightsQueryValidator (Application layer) depends on IFlightSearchHorizonOptions
        // rather than on AeroApiOptions directly, because Application must never reference
        // Infrastructure. This proves the translation registered in AddInfrastructure actually
        // reflects the real bound AeroApiOptions defaults (12 future / 3 past months), not just
        // that something resolves.
        var horizon = provider.GetRequiredService<IFlightSearchHorizonOptions>();

        Assert.Equal(12, horizon.MaxFutureMonths);
        Assert.Equal(3, horizon.MaxPastMonths);
    }

    [Fact]
    public void AddApplication_ResolvesSearchFlightsQueryValidator_WithItsHorizonOptionsDependency()
    {
        using var provider = BuildContainer();

        // The regression this test exists to catch: SearchFlightsQueryValidator gained a
        // constructor dependency (IFlightSearchHorizonOptions) in task 4. FluentValidation's
        // AddValidatorsFromAssembly scan constructs validators through the container, so a
        // missing registration for that dependency would only surface here — every other
        // validator test in the solution constructs validators with `new`, bypassing DI entirely.
        var validator = provider.GetRequiredService<IValidator<SearchFlightsQuery>>();

        Assert.IsType<SearchFlightsQueryValidator>(validator);
    }

    [Fact]
    public void AddInfrastructure_ResolvesAeroApiFlightSearchProvider_AsAScopedInstancePerScope()
    {
        using var root = BuildContainer();

        using (var scopeA = root.CreateScope())
        {
            var first = scopeA.ServiceProvider.GetRequiredService<AeroApiFlightSearchProvider>();
            var second = scopeA.ServiceProvider.GetRequiredService<AeroApiFlightSearchProvider>();
            Assert.Same(first, second); // same within a scope

            using var scopeB = root.CreateScope();
            var third = scopeB.ServiceProvider.GetRequiredService<AeroApiFlightSearchProvider>();
            Assert.NotSame(first, third); // different across scopes — proves it's Scoped, not Singleton
        }
    }

    [Fact]
    public void AddInfrastructure_ResolvesIAeroApiAccountUsageProvider_FromTheRealContainer()
    {
        using var provider = BuildContainer();

        var usage = provider.GetRequiredService<IAeroApiAccountUsageProvider>();

        Assert.IsType<AeroApiAccountUsageProvider>(usage);
    }

    [Fact]
    public void AddInfrastructure_ResolvesIAeroApiBudgetOptions_FromAeroApiOptions()
    {
        using var provider = BuildContainer();

        // The same translation as IFlightSearchHorizonOptions: proves the interface Application
        // owns reflects the real bound AeroApiOptions default ($5/month personal tier), not just
        // that something resolves.
        var budget = provider.GetRequiredService<IAeroApiBudgetOptions>();

        Assert.Equal(5m, budget.MonthlyBudgetUsd);
    }

    [Fact]
    public void AddInfrastructure_ResolvesIAeroApiThrottleObserver_AsTheVerySameGateInstance()
    {
        using var provider = BuildContainer();

        var gate = provider.GetRequiredService<AeroApiThrottleGate>();
        var observer = provider.GetRequiredService<IAeroApiThrottleObserver>();

        // A second gate would observe a slot queue nothing actually sends through — the whole
        // point of the singleton is that there is exactly one.
        Assert.Same(gate, observer);
    }

    [Fact]
    public void AddApplication_ResolvesGetAeroApiUsageQueryValidator_WithItsTimeProviderDependency()
    {
        using var provider = BuildContainer();

        // Same regression shape as SearchFlightsQueryValidator above: the validator takes a
        // TimeProvider (to enforce AeroAPI's one-year history cap), and only AddInfrastructure
        // registers one. Constructing it with `new` in a unit test would never notice.
        var validator = provider.GetRequiredService<IValidator<GetAeroApiUsageQuery>>();

        Assert.IsType<GetAeroApiUsageQueryValidator>(validator);
    }
}
