using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// Unit tests for <see cref="VerificationDatasetCache{T}"/> in isolation from either source --
/// the shared download-once/reuse-24h/release logic <c>OurAirportsVerificationSource</c> and
/// <c>MledozeCountriesVerificationSource</c> both delegate to. Internal type, visible here via the
/// existing <c>InternalsVisibleTo("FlightAssistant.Infrastructure.Tests")</c>.
/// </summary>
public class VerificationDatasetCacheTests
{
    private static VerificationDatasetCache<string> NewCache(TimeProvider timeProvider) =>
        new(timeProvider, TimeSpan.FromHours(24));

    [Fact]
    public async Task GetAsync_SecondCallWithinMaxAge_DoesNotReload()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        var first = await cache.GetAsync(Load, default);
        clock.Advance(TimeSpan.FromHours(23));
        var second = await cache.GetAsync(Load, default);

        Assert.Equal(1, loadCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetAsync_AfterMaxAge_Reloads()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        await cache.GetAsync(Load, default);
        clock.Advance(TimeSpan.FromHours(24));
        var second = await cache.GetAsync(Load, default);

        Assert.Equal(2, loadCount);
        Assert.Equal("v2", second);
    }

    [Fact]
    public async Task Release_MakesTheNextGetAsyncReload()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        Task<string> Load(CancellationToken ct) { loadCount++; return Task.FromResult("v" + loadCount); }

        await cache.GetAsync(Load, default);
        cache.Release();
        await cache.GetAsync(Load, default);

        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task GetAsync_ConcurrentCallsThatBothMissTheCache_LoadOnlyOnce()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;
        var gate = new SemaphoreSlim(0);

        async Task<string> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref loadCount);
            await gate.WaitAsync(ct);
            return "value";
        }

        var first = cache.GetAsync(Load, default);
        var second = cache.GetAsync(Load, default);
        await Task.Delay(50);
        gate.Release(2);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, loadCount);
        Assert.Equal("value", results[0]);
        Assert.Equal("value", results[1]);
    }

    [Fact]
    public async Task GetAsync_AFailedLoad_IsNotCached_TheNextCallRetriesImmediately()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = NewCache(clock);
        var loadCount = 0;

        Task<string> Load(CancellationToken ct)
        {
            loadCount++;
            return loadCount == 1
                ? Task.FromException<string>(new InvalidOperationException("boom"))
                : Task.FromResult("recovered");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Load, default));
        // No time advance -- a failure must not "poison" the cache for the rest of the 24h window.
        var second = await cache.GetAsync(Load, default);

        Assert.Equal(2, loadCount);
        Assert.Equal("recovered", second);
    }
}
