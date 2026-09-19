using System.Net;
using System.Text;
using FlightAssistant.Application;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Validators;
using NSubstitute;

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
    private static ServiceProvider BuildContainer(
        IDictionary<string, string?>? extraSettings = null,
        Action<IServiceCollection>? configure = null)
    {
        var settings = new Dictionary<string, string?> { ["AeroApi:ApiKey"] = "dummy-test-key" };
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
            settings[key] = value;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
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

        // ProviderRouteDefaults (Infrastructure) took IVerificationJobOptions as a second
        // constructor dependency in #531 task 7, so its For/IsCeilinged can answer for the three
        // verification routing keys as well as flight-search-route. The real registration lives
        // only in the API layer's AddVerificationJob (Program.cs), which this container never
        // calls, so every IFlightSearchProvider resolution below -- caching decorator ->
        // FlightSearchProviderSelector -> IFlightSearchRouter -> IProviderRouting ->
        // IProviderRouteDefaults -- would otherwise fail to resolve. FakeJobOptions already exists
        // for exactly this "any direct construction site needs one" reason (see its own doc
        // comment) and its DefaultSourcesFor mirrors the real ourairports/aeroapi/mledoze
        // configuration defaults. Registered as a singleton to match ProviderRouteDefaults' own
        // lifetime, the same pairing AddVerificationJob uses in production.
        services.AddSingleton<IVerificationJobOptions>(new FakeJobOptions());

        // Program.cs calls AddApplication() and AddInfrastructure() together — needed here so
        // SearchFlightsQueryValidator, which now takes IFlightSearchHorizonOptions in its
        // constructor, can actually be resolved the way FluentValidation's DI-scanned validators
        // are resolved in production, not constructed directly with a hand-supplied fake.
        services.AddApplication();

        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>Stands in for AeroAPI or AeroDataBox, answering a fixed 200 body and counting
    /// everything that reaches it. Defaults to an empty JSON object (what the AeroAPI-shaped tests
    /// need); the AeroDataBox route-search tests pass <c>{"departures":[]}</c> instead, since that
    /// provider looks for a "departures" array and would otherwise have nothing to iterate — either
    /// way the search comes back empty, which is all these DI tests need.</summary>
    private sealed class CountingTransport : HttpMessageHandler
    {
        private readonly string _body;

        public CountingTransport(string body = "{}") => _body = body;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
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
    public async Task AddInfrastructure_TheSearchChain_ChecksTheBudgetBetweenTheCacheAndAeroApi()
    {
        // The decorator tests build the chain by hand; this proves the registered one (#303). Past the
        // budget, a cached search is still answered and an uncached one is refused before any request.
        // A counting transport stands in for AeroAPI, so a wrong chain fails here without a real call.
        var transport = new CountingTransport();
        var budget = Substitute.For<IProviderBudgetGuard>();
        using var provider = BuildContainer(configure: services =>
        {
            services.AddScoped(_ => budget);
            services.AddHttpClient("AeroApi").ConfigurePrimaryHttpMessageHandler(() => transport);
        });
        var search = provider.GetRequiredService<IFlightSearchProvider>();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));

        budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
        await search.SearchAsync(new FlightSearchCriteria(null, null, date, "BA123", false));

        budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Exhausted);
        var cached = await search.SearchAsync(new FlightSearchCriteria(null, null, date, "BA123", false));
        var refused = await Assert.ThrowsAsync<DomainException>(
            () => search.SearchAsync(new FlightSearchCriteria(null, null, date, "BA124", false)));

        Assert.Empty(cached);
        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    /// <summary>
    /// #422, Correction 4: the selector sits inside the cache and outside the AeroAPI budget
    /// decorator, so a route search (both airports, no flight number) no longer touches AeroAPI at
    /// all once AeroDataBox is configured — it is answered even with the AeroAPI budget Exhausted.
    /// Genuinely red against the wiring before this task: the unmodified chain sends every search,
    /// route or not, straight past the budget decorator, which refuses ANY uncached search once
    /// Exhausted, so this route search was refused with a 503 rather than answered.
    /// </summary>
    [Fact]
    public async Task AddInfrastructure_RouteSearch_PastAeroApiBudget_IsAnsweredByAeroDataBox()
    {
        var aeroApiTransport = new CountingTransport();
        var aeroDataBoxTransport = new CountingTransport("""{"departures":[]}""");
        var budget = Substitute.For<IProviderBudgetGuard>();
        budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Exhausted);

        using var provider = BuildContainer(
            extraSettings: new Dictionary<string, string?> { ["AeroDataBox:ApiKey"] = "adb-dummy-test-key" },
            configure: services =>
            {
                services.AddScoped(_ => budget);
                services.AddHttpClient("AeroApi").ConfigurePrimaryHttpMessageHandler(() => aeroApiTransport);
                services.AddHttpClient(AeroDataBoxFlightSearchProvider.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => aeroDataBoxTransport);
            });
        var search = provider.GetRequiredService<IFlightSearchProvider>();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));

        var results = await search.SearchAsync(new FlightSearchCriteria("LHR", "JFK", date, null, false));

        Assert.Empty(results);
        Assert.Equal(2, aeroDataBoxTransport.Calls);
        Assert.Equal(0, aeroApiTransport.Calls);
    }

    /// <summary>
    /// #422, Correction 4: without AeroDataBox:ApiKey configured, the selector falls back to
    /// AeroAPI for every search — so a route search past the AeroAPI budget keeps the pre-#422
    /// behaviour (refused 503 naming the budget) and AeroDataBox is never touched.
    /// </summary>
    [Fact]
    public async Task AddInfrastructure_RouteSearch_AeroDataBoxKeyUnset_PastAeroApiBudget_IsRefused503()
    {
        var aeroApiTransport = new CountingTransport();
        var aeroDataBoxTransport = new CountingTransport("""{"departures":[]}""");
        var budget = Substitute.For<IProviderBudgetGuard>();
        budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Exhausted);

        using var provider = BuildContainer(configure: services =>
        {
            services.AddScoped(_ => budget);
            services.AddHttpClient("AeroApi").ConfigurePrimaryHttpMessageHandler(() => aeroApiTransport);
            services.AddHttpClient(AeroDataBoxFlightSearchProvider.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => aeroDataBoxTransport);
        });
        var search = provider.GetRequiredService<IFlightSearchProvider>();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));

        var refused = await Assert.ThrowsAsync<DomainException>(
            () => search.SearchAsync(new FlightSearchCriteria("LHR", "JFK", date, null, false)));

        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(0, aeroDataBoxTransport.Calls);
        Assert.Equal(0, aeroApiTransport.Calls);
    }

    [Fact]
    public void AddInfrastructure_ResolvesAeroDataBoxHttpClient_WithBaseAddressHeadersAndTimeoutConfigured()
    {
        using var provider = BuildContainer(new Dictionary<string, string?>
        {
            ["AeroDataBox:ApiKey"] = "adb-dummy-test-key",
        });
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = factory.CreateClient(AeroDataBoxFlightSearchProvider.HttpClientName);

        Assert.Equal(new Uri("https://aerodatabox.p.rapidapi.com/"), client.BaseAddress);
        Assert.True(client.DefaultRequestHeaders.Contains("X-RapidAPI-Key"));
        Assert.Equal("adb-dummy-test-key", client.DefaultRequestHeaders.GetValues("X-RapidAPI-Key").Single());
        Assert.True(client.DefaultRequestHeaders.Contains("X-RapidAPI-Host"));
        Assert.Equal(AeroDataBoxOptions.RapidApiHost, client.DefaultRequestHeaders.GetValues("X-RapidAPI-Host").Single());
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    /// <summary>
    /// #422 final review, finding 8 (carried D2): a null `Add(name, null)` still produces a header
    /// that IS present (per the GitHub client's own precedent test), so the `IsConfigured` guard at
    /// the AeroDataBox client registration is load-bearing. Green today; a mutation that adds the
    /// header unconditionally would turn this red.
    /// </summary>
    [Fact]
    public void AddInfrastructure_AeroDataBoxHttpClient_KeyUnset_SendsNoKeyHeader()
    {
        using var provider = BuildContainer();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = factory.CreateClient(AeroDataBoxFlightSearchProvider.HttpClientName);

        Assert.False(client.DefaultRequestHeaders.Contains("X-RapidAPI-Key"));
        Assert.True(client.DefaultRequestHeaders.Contains("X-RapidAPI-Host"));
    }

    /// <summary>
    /// #422 final review, finding 2 (security): the primary handler must not follow redirects, or
    /// X-RapidAPI-Key would be replayed to whatever host a 3xx names — mirrors
    /// GitHubDependencyInjectionTests.NeitherClient_FollowsRedirects.
    /// </summary>
    [Fact]
    public void AddInfrastructure_AeroDataBoxClient_DoesNotFollowRedirects()
    {
        using var provider = BuildContainer();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(AeroDataBoxFlightSearchProvider.HttpClientName);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
    }

    /// <summary>
    /// #422 final review, finding 3 (carried D1): TimeoutSeconds=0 must fail
    /// AddOptions&lt;AeroDataBoxOptions&gt;()'s validation, the same [Range] precedent as
    /// AeroApiOptions.TimeoutSeconds. Accessing .Value runs the data-annotation validation
    /// ValidateOnStart runs at startup — same pattern as GitHubDependencyInjectionTests.
    /// </summary>
    [Fact]
    public void AddInfrastructure_AeroDataBoxTimeoutSecondsZero_FailsOptionsValidation()
    {
        using var provider = BuildContainer(new Dictionary<string, string?>
        {
            ["AeroDataBox:ApiKey"] = "adb-dummy-test-key",
            ["AeroDataBox:TimeoutSeconds"] = "0",
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AeroDataBoxOptions>>().Value);
    }

    /// <summary>
    /// #422 final review, finding 3 (carried D1): the key stays deliberately NOT [Required] — an
    /// unset key with otherwise-valid options must not stop IFlightSearchProvider (and therefore
    /// every GET /flights/search request, route or flight-number) from resolving.
    /// </summary>
    [Fact]
    public void AddInfrastructure_AeroDataBoxKeyUnset_ValidOtherwise_IFlightSearchProviderStillResolves()
    {
        using var provider = BuildContainer();

        var search = provider.GetRequiredService<IFlightSearchProvider>();

        Assert.NotNull(search);
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
    public void AddApplication_ResolvesTrackFlightCommandValidator_WithItsTimeProviderDependency()
    {
        using var provider = BuildContainer();

        // TrackFlightCommandValidator takes a TimeProvider (#350). As with the test above, only
        // building it through the container would expose a missing registration.
        var validator = provider.GetRequiredService<IValidator<TrackFlightCommand>>();

        Assert.IsType<TrackFlightCommandValidator>(validator);
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
        Assert.Equal(0.8m, budget.SoftLimitRatio);
    }

    [Fact]
    public void AddInfrastructure_ResolvesIProviderBudgetGuard_AsProviderBudgetGuard()
    {
        using var provider = BuildContainer();

        // The guard's own tests construct it by hand; this proves the host can build it (#303) from
        // the usage provider, the cache, the budget options and the TimeProvider AddInfrastructure registers.
        var guard = provider.GetRequiredService<IProviderBudgetGuard>();

        Assert.IsType<ProviderBudgetGuard>(guard);
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

    [Fact]
    public void AddInfrastructure_ResolvesIFlightStatusWindowOptions_FromTheBoundAeroApiOptions()
    {
        // FlightSyncPlanner starts Live polling this many days before departure (#343). A
        // non-default value, so a translation that ignores configuration can't pass.
        using var provider = BuildContainer(new Dictionary<string, string?> { ["AeroApi:StatusWindowFutureDays"] = "1" });

        Assert.Equal(1, provider.GetRequiredService<IFlightStatusWindowOptions>().StatusWindowFutureDays);
    }
}
