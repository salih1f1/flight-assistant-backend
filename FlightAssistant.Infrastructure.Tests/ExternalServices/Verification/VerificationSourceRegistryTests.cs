using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// <see cref="VerificationSourceRegistry"/> against fake <see cref="IVerificationSource"/>s --
/// pure name resolution and selective release, no HTTP involved (that is each source's own
/// responsibility, covered in its own test file).
/// </summary>
public class VerificationSourceRegistryTests
{
    private static IVerificationSource FakeSource(string name)
    {
        var source = Substitute.For<IVerificationSource>();
        source.Name.Returns(name);
        source.CostPerRowUsd.Returns(0m);
        source.SupportedEntityTypes.Returns(new[] { VerificationEntityType.Airport });
        return source;
    }

    [Fact]
    public void TryGet_ResolvesARegisteredSourceByName()
    {
        var a = FakeSource("a");
        var registry = new VerificationSourceRegistry(new[] { a, FakeSource("b") });

        Assert.Same(a, registry.TryGet("a"));
    }

    [Fact]
    public void TryGet_ReturnsNull_ForAnUnregisteredName()
    {
        var registry = new VerificationSourceRegistry(new[] { FakeSource("a") });

        Assert.Null(registry.TryGet("aeroapi"));
    }

    [Fact]
    public void TryGet_ReturnsNull_ForABlankName()
    {
        var registry = new VerificationSourceRegistry(new[] { FakeSource("a") });

        Assert.Null(registry.TryGet(""));
    }

    [Fact]
    public void Sources_EnumeratesEveryRegisteredSource()
    {
        var a = FakeSource("a");
        var b = FakeSource("b");
        var registry = new VerificationSourceRegistry(new[] { a, b });

        Assert.Equal(new[] { a, b }, registry.Sources);
    }

    [Fact]
    public void ReleaseUnused_ReleasesOnlySourcesNotInTheGivenSet()
    {
        var inUse = FakeSource("in-use");
        var unused = FakeSource("unused");
        var registry = new VerificationSourceRegistry(new[] { inUse, unused });

        registry.ReleaseUnused(new[] { "in-use" }, TimeSpan.Zero);

        inUse.DidNotReceiveWithAnyArgs().ReleaseCache(default);
        unused.Received(1).ReleaseCache(TimeSpan.Zero);
    }

    [Fact]
    public void ReleaseUnused_WithAnEmptySet_ReleasesEverySource()
    {
        var a = FakeSource("a");
        var b = FakeSource("b");
        var registry = new VerificationSourceRegistry(new[] { a, b });

        registry.ReleaseUnused(Array.Empty<string>(), TimeSpan.Zero);

        a.Received(1).ReleaseCache(TimeSpan.Zero);
        b.Received(1).ReleaseCache(TimeSpan.Zero);
    }

    /// <summary>
    /// #378, over the real dataset cache: a source an open request uses is never released, however
    /// long its dataset has gone unused; an unused source is released once idle for the period it is
    /// given, and kept while it was looked up more recently than that.
    /// </summary>
    [Fact]
    public async Task ReleaseUnused_KeepsASourceInUseHoweverIdle_AndAnUnusedOneUsedWithinTheIdlePeriod()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var inUse = new CachingSource("in-use", clock);
        var idle = new CachingSource("idle", clock);
        var recent = new CachingSource("recent", clock);
        var registry = new VerificationSourceRegistry(new IVerificationSource[] { inUse, idle, recent });

        await inUse.LookupOnceAsync();
        await idle.LookupOnceAsync();
        clock.Advance(TimeSpan.FromHours(23));
        await recent.LookupOnceAsync();
        clock.Advance(TimeSpan.FromMinutes(10));

        registry.ReleaseUnused(new[] { "in-use" }, TimeSpan.FromMinutes(30));

        Assert.True(inUse.HasCachedData);   // unused for 23h10m, but an open request names it
        Assert.False(idle.HasCachedData);   // unused for 23h10m
        Assert.True(recent.HasCachedData);  // looked up 10 minutes ago
    }

    /// <summary>A source over the real <see cref="VerificationDatasetCache{T}"/>, as both free sources
    /// are, so release follows the real idle rule without any HTTP.</summary>
    private sealed class CachingSource(string name, TimeProvider clock) : IVerificationSource
    {
        private readonly VerificationDatasetCache<string> _cache = new(clock, TimeSpan.FromHours(24));

        public string Name => name;
        public decimal CostPerRowUsd => 0m;
        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes { get; } = new[] { VerificationEntityType.Airport };
        public bool HasCachedData => _cache.HasCachedData;

        public async Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            await _cache.GetAsync(_ => Task.FromResult("dataset"), ct);
            return VerificationSourceFetchResult.NotFound;
        }

        public Task LookupOnceAsync() =>
            LookupAsync(VerificationEntityType.Airport, VerificationSourceLookupKey.ForAirport("LHR", null));

        public void ReleaseCache(TimeSpan idleRelease) => _cache.Release(idleRelease);
    }

    [Fact]
    public void AnySourceHasCachedData_IsTrueOnlyWhileSomeSourceHoldsData()
    {
        var a = FakeSource("a");
        var b = FakeSource("b");
        var registry = new VerificationSourceRegistry(new[] { a, b });

        Assert.False(registry.AnySourceHasCachedData);

        b.HasCachedData.Returns(true);
        Assert.True(registry.AnySourceHasCachedData);
    }
}
