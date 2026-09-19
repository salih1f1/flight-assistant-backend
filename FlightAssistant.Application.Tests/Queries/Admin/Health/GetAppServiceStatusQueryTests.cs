using FlightAssistant.Application.Hosting;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Health;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Health;

/// <summary>
/// Mirrors <c>GetAzureBudgetQueryTests</c> (#530 task 4): which <see cref="AppServiceState"/>s are
/// cached, for how long, and that the snapshot's fields reach the DTO unchanged.
/// </summary>
public class GetAppServiceStatusQueryTests
{
    /// <summary>Real dictionary-backed cache rather than a substitute — following
    /// <c>GetAzureBudgetQueryTests.FakeCache</c> — so a hit here means the handler actually wrote
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

    private static AppServiceSnapshot OkSnapshot() => new(
        AppServiceState.Ok, "site-a", "Running", "Normal", true, true, 2, null);

    private static GetAppServiceStatusQueryHandler BuildHandler(IAppServiceProvider provider, FakeCache? cache = null) =>
        new(provider, cache ?? new FakeCache());

    [Fact]
    public async Task ReadsThroughToTheProvider_OnAColdCache()
    {
        var provider = Substitute.For<IAppServiceProvider>();
        provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());

        await BuildHandler(provider).Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

        await provider.Received(1).GetStatusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASecondCallInsideTheWindow_IsServedFromCache()
    {
        var provider = Substitute.For<IAppServiceProvider>();
        provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());
        var handler = BuildHandler(provider);

        await handler.Handle(new GetAppServiceStatusQuery(), CancellationToken.None);
        await handler.Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

        await provider.Received(1).GetStatusAsync(Arg.Any<CancellationToken>());
    }

    // Always On is something an operator changes and then immediately wants reflected, so a refusal
    // or outage must not sit on screen for the whole window.
    [Fact]
    public async Task ARefusalIsNotCached()
    {
        var provider = Substitute.For<IAppServiceProvider>();
        provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(AppServiceSnapshot.NotAuthorized());
        var handler = BuildHandler(provider);

        await handler.Handle(new GetAppServiceStatusQuery(), CancellationToken.None);
        await handler.Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

        await provider.Received(2).GetStatusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheStateReachesTheDtoAsItsName()
    {
        var provider = Substitute.For<IAppServiceProvider>();
        provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(AppServiceSnapshot.NotConfigured());

        var result = await BuildHandler(provider).Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

        Assert.Equal(nameof(AppServiceState.NotConfigured), result.State);
    }

    [Fact]
    public async Task EveryFieldReachesTheDto()
    {
        var provider = Substitute.For<IAppServiceProvider>();
        provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(
            new AppServiceSnapshot(AppServiceState.Ok, "site-a", "Running", "Normal", true, false, 3, null));

        var result = await BuildHandler(provider).Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

        Assert.Equal("site-a", result.SiteName);
        Assert.Equal("Running", result.SiteState);
        Assert.Equal("Normal", result.AvailabilityState);
        Assert.True(result.Enabled);
        Assert.False(result.AlwaysOn);
        Assert.Equal(3, result.InstanceCount);
    }

    /// <summary>Pins the TTL itself, not just "a second call within it hits the cache" — a TTL that
    /// silently drifted from five minutes would pass every other test in this file. Not in the task
    /// brief's own test list; added to match <c>GetAzureBudgetQueryTests.CachesTheOkSnapshotForExactlyOneHour</c>,
    /// the one assertion in that model file with no equivalent here otherwise.</summary>
    [Fact]
    public async Task CachesTheOkSnapshotForExactlyFiveMinutes()
    {
        var provider = Substitute.For<IAppServiceProvider>();
        provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());
        var cache = new FakeCache();

        await BuildHandler(provider, cache).Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

        var write = Assert.Single(cache.Writes);
        Assert.Equal("hosting:app-service", write.Key);
        Assert.Equal(TimeSpan.FromMinutes(5), write.Ttl);
    }
}
