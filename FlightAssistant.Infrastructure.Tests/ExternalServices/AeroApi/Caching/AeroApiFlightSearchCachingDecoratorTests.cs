using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Providers;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using FlightAssistant.Infrastructure.ExternalServices;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi.Caching;

/// <summary>
/// Drives <see cref="AeroApiFlightSearchCachingDecorator"/> against a counting fake
/// <see cref="IFlightSearchProvider"/>, asserting on call counts rather than "by eye" per issue
/// #282's own acceptance criterion. Most tests run against the real <see cref="MemoryCacheService"/>
/// (exercising genuine cache-miss/hit behaviour, including a stored empty list staying distinguishable
/// from "not found") rather than a mock that would just echo back whatever was stored.
/// </summary>
public class AeroApiFlightSearchCachingDecoratorTests
{
    /// <summary>Counts every call that actually reached the wrapped provider.</summary>
    private sealed class CountingFlightSearchProvider(
        Func<FlightSearchCriteria, IReadOnlyList<FlightSearchResult>> respond) : IFlightSearchProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(respond(criteria));
        }
    }

    private static readonly FlightSearchResult SampleResult = new(
        FlightNumber: "BA123",
        ServiceDate: new DateOnly(2026, 9, 15),
        DepartureAirportIata: "LHR",
        ArrivalAirportIata: "JFK",
        ScheduledDepartureUtc: new DateTime(2026, 9, 15, 10, 30, 0, DateTimeKind.Utc),
        ScheduledArrivalUtc: new DateTime(2026, 9, 15, 13, 45, 0, DateTimeKind.Utc));

    private static FlightSearchCriteria Criteria(
        string? departure = "LHR",
        string? arrival = "JFK",
        DateOnly? date = null,
        string? flightNumber = null,
        bool includeCodeshares = false) =>
        new(departure, arrival, date ?? new DateOnly(2026, 9, 15), flightNumber, includeCodeshares);

    private static ICacheService NewMemoryCache() => new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()));

    /// <summary>
    /// A router stub that resolves every criteria to the same fixed provider, so tests that don't
    /// care about the routing dimension (everything except the two #531 tests below) see the same
    /// provider on every call and the provider portion of the key never varies underneath them.
    /// </summary>
    private static IFlightSearchRouter StubRouter(string provider = ProviderJobs.AeroDataBox)
    {
        var router = Substitute.For<IFlightSearchRouter>();
        router.ResolveAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>()).Returns(provider);
        return router;
    }

    private static AeroApiFlightSearchCachingDecorator Decorator(
        IFlightSearchProvider inner,
        ICacheService? cache = null,
        FakeTimeProvider? clock = null,
        TimeSpan? ttlPastOrToday = null,
        TimeSpan? ttlFuture = null,
        IFlightSearchRouter? router = null) =>
        new(
            inner,
            router ?? StubRouter(),
            cache ?? NewMemoryCache(),
            Options.Create(new AeroApiOptions
            {
                ApiKey = "dummy",
                SearchCacheTtlPastOrToday = ttlPastOrToday ?? TimeSpan.FromMinutes(5),
                SearchCacheTtlFuture = ttlFuture ?? TimeSpan.FromHours(12)
            }),
            clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<AeroApiFlightSearchCachingDecorator>.Instance);

    [Fact]
    public async Task SearchAsync_RepeatedIdenticalSearch_IssuesZeroUpstreamCallsOnTheSecondRequest()
    {
        var inner = new CountingFlightSearchProvider(_ => [SampleResult]);
        var decorator = Decorator(inner);
        var criteria = Criteria();

        await decorator.SearchAsync(criteria);
        var second = await decorator.SearchAsync(criteria);

        Assert.Equal(1, inner.Calls);
        var result = Assert.Single(second);
        Assert.Equal(SampleResult, result);
    }

    [Fact]
    public async Task SearchAsync_DifferingCriteria_ProduceSeparateCacheEntries()
    {
        var inner = new CountingFlightSearchProvider(_ => [SampleResult]);
        var decorator = Decorator(inner);

        await decorator.SearchAsync(Criteria(flightNumber: "BA123"));
        await decorator.SearchAsync(Criteria(flightNumber: "BA456"));

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task SearchAsync_CaseAndWhitespaceDifferences_ShareOneCacheEntry()
    {
        var inner = new CountingFlightSearchProvider(_ => [SampleResult]);
        var decorator = Decorator(inner);

        await decorator.SearchAsync(Criteria(departure: "lhr", arrival: " jfk "));
        await decorator.SearchAsync(Criteria(departure: " LHR ", arrival: "JFK"));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task SearchAsync_NullDepartureVersusEmptyDeparture_DoNotCollide()
    {
        var inner = new CountingFlightSearchProvider(_ => [SampleResult]);
        var decorator = Decorator(inner);

        await decorator.SearchAsync(Criteria(departure: null, flightNumber: "BA123"));
        await decorator.SearchAsync(Criteria(departure: "", flightNumber: "BA123"));

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task SearchAsync_EmptyResult_IsCached_AndReturnsEmptyOnTheHitRatherThanMissing()
    {
        var inner = new CountingFlightSearchProvider(_ => []);
        var decorator = Decorator(inner);
        var criteria = Criteria();

        var first = await decorator.SearchAsync(criteria);
        var second = await decorator.SearchAsync(criteria);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.NotNull(second);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task SearchAsync_DomainException_IsNotCached_AndTheNextCallRetriesUpstream()
    {
        var attempt = 0;
        var inner = new CountingFlightSearchProvider(_ =>
        {
            attempt++;
            if (attempt == 1) throw new DomainException("Flight search is temporarily unavailable.", 502);
            return [SampleResult];
        });
        var decorator = Decorator(inner);
        var criteria = Criteria();

        await Assert.ThrowsAsync<DomainException>(() => decorator.SearchAsync(criteria));
        var second = await decorator.SearchAsync(criteria);

        Assert.Equal(2, inner.Calls);
        Assert.Single(second);
    }

    [Theory]
    [InlineData(2026, 1, 1, 2026, 1, 1, true)]   // Date == today (UTC) -> past/today TTL
    [InlineData(2026, 1, 1, 2025, 12, 31, true)] // Date in the past -> past/today TTL
    [InlineData(2026, 1, 1, 2026, 1, 2, false)]  // Date in the future -> future TTL
    public async Task SearchAsync_SelectsTtlByDateRelativeToToday_FromTheInjectedTimeProvider(
        int todayY, int todayM, int todayD, int dateY, int dateM, int dateD, bool expectPastOrTodayTtl)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(todayY, todayM, todayD, 0, 0, 0, TimeSpan.Zero));
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<List<FlightSearchResult>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<FlightSearchResult>?>(null));
        var inner = new CountingFlightSearchProvider(_ => [SampleResult]);
        var ttlPastOrToday = TimeSpan.FromMinutes(5);
        var ttlFuture = TimeSpan.FromHours(12);
        var decorator = Decorator(inner, cache, clock, ttlPastOrToday, ttlFuture);
        var criteria = Criteria(date: new DateOnly(dateY, dateM, dateD));

        await decorator.SearchAsync(criteria);

        var expectedTtl = expectPastOrTodayTtl ? ttlPastOrToday : ttlFuture;
        await cache.Received(1).SetAsync(
            Arg.Any<string>(), Arg.Any<List<FlightSearchResult>>(), expectedTtl, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_RoundTripsDateOnlyAndDateTimeExactly_ThroughARedisBackedCache()
    {
        // RedisCacheService is what ICacheService actually resolves to whenever ConnectionString:Redis
        // is configured (CachingServiceCollectionExtensions.AddAppCaching) — unlike MemoryCacheService,
        // it goes through System.Text.Json. This proves FlightSearchResult's DateOnly and DateTime
        // fields survive that serialization exactly, using a real in-process IDistributedCache
        // (MemoryDistributedCache) rather than requiring a live Redis instance.
        var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cache = new RedisCacheService(distributed, new NoOpDataSourceObserver(), TimeProvider.System);
        var preciseResult = new FlightSearchResult(
            FlightNumber: "QF1",
            ServiceDate: new DateOnly(2028, 2, 29), // leap day — an easy DateOnly edge case
            DepartureAirportIata: "SYD",
            ArrivalAirportIata: "LHR",
            ScheduledDepartureUtc: new DateTime(2028, 2, 29, 23, 59, 59, 123, DateTimeKind.Utc),
            ScheduledArrivalUtc: new DateTime(2028, 3, 1, 6, 15, 0, DateTimeKind.Utc));
        var inner = new CountingFlightSearchProvider(_ => [preciseResult]);
        var decorator = Decorator(inner, cache);
        var criteria = Criteria(date: preciseResult.ServiceDate);

        await decorator.SearchAsync(criteria);
        var roundTripped = await decorator.SearchAsync(criteria);

        Assert.Equal(1, inner.Calls);
        var result = Assert.Single(roundTripped);
        Assert.Equal(preciseResult, result);
        Assert.Equal(DateTimeKind.Utc, result.ScheduledDepartureUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.ScheduledArrivalUtc.Kind);
    }

    [Fact]
    public async Task SearchAsync_SameCriteriaUnderTwoProviders_DoesNotShareACacheEntry()
    {
        // The defect this dimension prevents: before #531 the key was dep|arr|date|fn|cs with no
        // provider in it, so flipping the route switch served the other provider's rows for up to
        // SearchCacheTtlFuture (12 h). That used to need a deploy and a documented prefix flush;
        // a runtime switch would make it a per-button-press problem.
        var criteria = Criteria(departure: "SOF", arrival: "IST", date: new DateOnly(2026, 12, 1));
        var responses = new Queue<List<FlightSearchResult>>(new[]
        {
            new List<FlightSearchResult> { SampleResult with { FlightNumber = "FB101" } },
            new List<FlightSearchResult> { SampleResult with { FlightNumber = "FB202" } }
        });
        var inner = new CountingFlightSearchProvider(_ => responses.Dequeue());
        var router = Substitute.For<IFlightSearchRouter>();
        router.ResolveAsync(criteria, Arg.Any<CancellationToken>()).Returns(ProviderJobs.AeroDataBox);
        var decorator = Decorator(inner, router: router);

        var first = await decorator.SearchAsync(criteria);

        // The switch is flipped. The same criteria must now reach the provider, not the cache.
        router.ResolveAsync(criteria, Arg.Any<CancellationToken>()).Returns(ProviderJobs.AeroApi);
        var second = await decorator.SearchAsync(criteria);

        Assert.Equal("FB101", Assert.Single(first).FlightNumber);
        Assert.Equal("FB202", Assert.Single(second).FlightNumber);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task SearchAsync_SameCriteriaSameProvider_StillHitsTheCache()
    {
        var criteria = Criteria(departure: "SOF", arrival: "IST", date: new DateOnly(2026, 12, 1));
        var inner = new CountingFlightSearchProvider(_ => [SampleResult with { FlightNumber = "FB101" }]);
        var router = Substitute.For<IFlightSearchRouter>();
        router.ResolveAsync(criteria, Arg.Any<CancellationToken>()).Returns(ProviderJobs.AeroDataBox);
        var decorator = Decorator(inner, router: router);

        await decorator.SearchAsync(criteria);
        await decorator.SearchAsync(criteria);

        Assert.Equal(1, inner.Calls);
    }
}
