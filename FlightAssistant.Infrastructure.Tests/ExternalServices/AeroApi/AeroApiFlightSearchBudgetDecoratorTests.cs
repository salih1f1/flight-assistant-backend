using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Providers;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>Past the budget guard's soft limit, a search that would reach AeroAPI is refused (#303).</summary>
public class AeroApiFlightSearchBudgetDecoratorTests
{
    /// <summary>Counts every search that reached the wrapped provider.</summary>
    private sealed class CountingFlightSearchProvider : IFlightSearchProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<FlightSearchResult>> SearchAsync(FlightSearchCriteria criteria, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<FlightSearchResult>>([Row]);
        }
    }

    private static readonly FlightSearchResult Row = new(
        FlightNumber: "BA123",
        ServiceDate: new DateOnly(2026, 9, 29),
        DepartureAirportIata: "LHR",
        ArrivalAirportIata: "JFK",
        ScheduledDepartureUtc: new DateTime(2026, 9, 29, 10, 30, 0, DateTimeKind.Utc),
        ScheduledArrivalUtc: new DateTime(2026, 9, 29, 13, 45, 0, DateTimeKind.Utc));

    private static readonly FlightSearchCriteria LhrJfk = new("LHR", "JFK", new DateOnly(2026, 9, 29), null, false);
    private static readonly FlightSearchCriteria LhrCdg = new("LHR", "CDG", new DateOnly(2026, 9, 29), null, false);

    private readonly CountingFlightSearchProvider _inner = new();
    private readonly IProviderBudgetGuard _budget = Substitute.For<IProviderBudgetGuard>();

    private AeroApiFlightSearchBudgetDecorator Decorator() =>
        new(_inner, _budget, NullLogger<AeroApiFlightSearchBudgetDecorator>.Instance);

    private void BudgetIs(ProviderBudgetState state) =>
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(state);

    [Fact]
    public async Task SearchAsync_WithinBudget_CallsTheProvider()
    {
        BudgetIs(ProviderBudgetState.Normal);

        var results = await Decorator().SearchAsync(LhrJfk);

        Assert.Equal(Row, Assert.Single(results));
        Assert.Equal(1, _inner.Calls);
    }

    [Theory]
    [InlineData(ProviderBudgetState.SoftLimit,
        "Flight search is paused: this month's flight data budget is nearly used up. Please try again later.")]
    [InlineData(ProviderBudgetState.Exhausted,
        "Flight search is paused: this month's flight data budget is used up. Please try again later.")]
    public async Task SearchAsync_PastTheSoftLimit_Throws503NamingTheBudget_WithoutCallingTheProvider(
        ProviderBudgetState state, string message)
    {
        BudgetIs(state);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Decorator().SearchAsync(LhrJfk));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(message, ex.Message);
        Assert.Equal(0, _inner.Calls);
    }

    [Fact]
    public async Task SearchAsync_BehindTheCache_ACachedSearchIsStillAnswered_PastTheBudget()
    {
        // The router dimension is irrelevant here — this test is about budget-vs-cache ordering,
        // not routing — so it resolves to a fixed provider (AeroApi, matching what this decorator
        // stack actually wraps in production) on every call.
        var router = Substitute.For<IFlightSearchRouter>();
        router.ResolveAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(ProviderJobs.AeroApi);

        var search = new AeroApiFlightSearchCachingDecorator(
            Decorator(),
            router,
            new MemoryCacheService(new MemoryCache(new MemoryCacheOptions())),
            Options.Create(new AeroApiOptions { ApiKey = "dummy" }),
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<AeroApiFlightSearchCachingDecorator>.Instance);

        BudgetIs(ProviderBudgetState.Normal);
        await search.SearchAsync(LhrJfk);

        BudgetIs(ProviderBudgetState.Exhausted);
        var cached = await search.SearchAsync(LhrJfk);
        var refused = await Assert.ThrowsAsync<DomainException>(() => search.SearchAsync(LhrCdg));

        Assert.Equal(Row, Assert.Single(cached));
        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(1, _inner.Calls);
    }
}
