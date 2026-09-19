using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Application.Usage;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Usage;

/// <summary>
/// What carries the risk here is narrower than <c>GetAeroApiUsageQueryTests</c> — there is no range
/// to resolve — so these tests pin down the one real decision: which
/// <see cref="AzureBudgetState"/>s are cached, for how long, and that the snapshot's fields reach
/// the DTO unchanged.
///
/// <para>
/// <b>DataAsOfUtc (#496 tasks 2 and 4).</b> No test here asserts that a non-null
/// <c>DataAsOfUtc</c> reaches the DTO: neither Cost Management endpoint the provider reads reports
/// one (verified against the REST reference for both, task 2), so no snapshot this suite can
/// honestly construct ever carries a non-null value to map through. <see cref="MapsEverySnapshotFieldToTheDto"/>
/// asserts the field survives as null instead, which is the one behaviour that is actually true today.
/// </para>
/// </summary>
public class GetAzureBudgetQueryTests
{
    /// <summary>Real dictionary-backed cache rather than a substitute — following
    /// <c>GetAeroApiUsageQueryTests.FakeCache</c> — so a hit here means the handler actually wrote
    /// and read back the same key, not that a stub was told to say yes.</summary>
    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, object?> _entries = [];
        public List<(string Key, TimeSpan? Ttl)> Writes { get; } = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            _entries[key] = value;
            Writes.Add((key, absoluteExpire));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private static AzureBudgetSnapshot OkSnapshot() => new(
        AzureBudgetState.Ok, "monthly", 500m, 123.45m, "USD",
        new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        DataAsOfUtc: null, Message: null);

    private static AzureBudgetSnapshot NoBudgetSnapshot() => new(
        AzureBudgetState.NoBudget, null, null, 42.10m, "EUR", null, null, null, null);

    private static GetAzureBudgetQueryHandler BuildHandler(IAzureCostProvider provider, FakeCache? cache = null) =>
        new(provider, cache ?? new FakeCache());

    [Fact]
    public async Task ReadsThroughToTheProvider_OnAColdCache()
    {
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());

        await BuildHandler(provider).Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        await provider.Received(1).GetBudgetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASecondCallInsideTheWindow_IsServedFromCache()
    {
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());
        var handler = BuildHandler(provider);

        await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);
        await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        // Cost Management rate-limits aggressively and the data moves every few hours at best.
        await provider.Received(1).GetBudgetAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>The cache decision's other half: NoBudget is answered from a clean, authorized
    /// response just as much as Ok is, so it is cached the same way. A handler that only special-
    /// cased <c>State == Ok</c> would pass every other test here and still be wrong.</summary>
    [Fact]
    public async Task ANoBudgetAnswerIsAlsoServedFromCache()
    {
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(NoBudgetSnapshot());
        var handler = BuildHandler(provider);

        await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);
        await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        await provider.Received(1).GetBudgetAsync(Arg.Any<CancellationToken>());
    }

    // Caching a refusal would leave "not authorized" on screen for an hour after the role was granted,
    // with nothing an operator could do to refresh it.
    [Fact]
    public async Task ARefusalIsNotCached()
    {
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(AzureBudgetSnapshot.NotAuthorized());
        var handler = BuildHandler(provider);

        await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);
        await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        await provider.Received(2).GetBudgetAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>An outage is exactly as cheap to retry as a refusal, and exactly as annoying to
    /// serve stale, so it gets the same treatment as <see cref="ARefusalIsNotCached"/>.</summary>
    [Fact]
    public async Task AnOutageIsNotCached()
    {
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(AzureBudgetSnapshot.Unavailable("Timed out."));
        var handler = BuildHandler(provider);

        await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);
        await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        await provider.Received(2).GetBudgetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheStateReachesTheDtoAsItsName()
    {
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(AzureBudgetSnapshot.NotConfigured());

        var result = await BuildHandler(provider).Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        Assert.Equal(nameof(AzureBudgetState.NotConfigured), result.State);
    }

    /// <summary>Every field survives the trip from snapshot to DTO unchanged, including a non-USD
    /// currency — the subscription's billing currency is never assumed to be USD — and including
    /// <c>DataAsOfUtc</c> staying null, which is the only value it can honestly carry today.</summary>
    [Fact]
    public async Task MapsEverySnapshotFieldToTheDto()
    {
        var snapshot = OkSnapshot();
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(snapshot);

        var result = await BuildHandler(provider).Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        Assert.Equal(nameof(AzureBudgetState.Ok), result.State);
        Assert.Equal(snapshot.BudgetName, result.BudgetName);
        Assert.Equal(snapshot.BudgetAmount, result.BudgetAmount);
        Assert.Equal(snapshot.CurrentSpendAmount, result.CurrentSpendAmount);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(snapshot.PeriodStartUtc, result.PeriodStartUtc);
        Assert.Equal(snapshot.PeriodEndUtc, result.PeriodEndUtc);
        Assert.Null(result.DataAsOfUtc);
        Assert.Null(result.Message);
    }

    /// <summary>Same as <see cref="MapsEverySnapshotFieldToTheDto"/>, with a non-USD currency —
    /// the field this repository's own verification checklist calls out by name.</summary>
    [Fact]
    public async Task ANonUsdCurrencySurvivesToTheDto()
    {
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(NoBudgetSnapshot());

        var result = await BuildHandler(provider).Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        Assert.Equal(nameof(AzureBudgetState.NoBudget), result.State);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal(42.10m, result.CurrentSpendAmount);
    }

    /// <summary>Pins the TTL itself, not just "a second call within it hits the cache" — a TTL that
    /// silently drifted from one hour would pass every other test in this file.</summary>
    [Fact]
    public async Task CachesTheOkSnapshotForExactlyOneHour()
    {
        var provider = Substitute.For<IAzureCostProvider>();
        provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());
        var cache = new FakeCache();

        await BuildHandler(provider, cache).Handle(new GetAzureBudgetQuery(), CancellationToken.None);

        var write = Assert.Single(cache.Writes);
        Assert.Equal("azure:budget", write.Key);
        Assert.Equal(TimeSpan.FromHours(1), write.Ttl);
    }
}
