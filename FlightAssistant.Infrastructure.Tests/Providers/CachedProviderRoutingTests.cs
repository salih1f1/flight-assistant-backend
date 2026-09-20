using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.Providers;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.Providers
{
    public class CachedProviderRoutingTests
    {
        private static readonly IReadOnlyList<string> ConfiguredDefault = new[] { "aerodatabox" };

        private static CachedProviderRouting Build(
            ProviderRouteReader read,
            FakeTimeProvider time) =>
            new(read, _ => ConfiguredDefault, time, NullLogger<CachedProviderRouting>.Instance);

        [Fact]
        public async Task GetProvidersAsync_NoRow_ReturnsTheConfiguredDefault()
        {
            var time = new FakeTimeProvider();
            var routing = Build((_, _) => Task.FromResult<ProviderRouteState?>(null), time);

            var providers = await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(new[] { "aerodatabox" }, providers);
        }

        [Fact]
        public async Task GetProvidersAsync_ReadsOnceInsideTheCacheWindow()
        {
            var time = new FakeTimeProvider();
            var reads = 0;
            var routing = Build((job, _) =>
            {
                reads++;
                return Task.FromResult<ProviderRouteState?>(
                    new ProviderRouteState(job, new[] { "aeroapi" }, false, null, null, null));
            }, time);

            await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);
            time.Advance(CachedProviderRouting.CacheWindow - TimeSpan.FromMilliseconds(1));
            var second = await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(1, reads);
            Assert.Equal(new[] { "aeroapi" }, second);
        }

        [Fact]
        public async Task GetProvidersAsync_ReadsAgainOnceTheWindowHasPassed()
        {
            var time = new FakeTimeProvider();
            var reads = 0;
            var routing = Build((job, _) =>
            {
                reads++;
                return Task.FromResult<ProviderRouteState?>(
                    new ProviderRouteState(job, new[] { "aeroapi" }, false, null, null, null));
            }, time);

            await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);
            time.Advance(CachedProviderRouting.CacheWindow);
            await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(2, reads);
        }

        [Fact]
        public async Task GetProvidersAsync_ReadFailure_FallsBackToTheConfiguredDefault()
        {
            // Unlike CachedJobSwitch, there is no fail-open/fail-closed split to make: a routing
            // read failure has one unambiguous right answer, because configuration is always
            // available in-process.
            var time = new FakeTimeProvider();
            var routing = Build((_, _) => throw new InvalidOperationException("no database"), time);

            var providers = await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(new[] { "aerodatabox" }, providers);
        }

        [Fact]
        public async Task GetAsync_NoRow_ReportsTheDefaultAsDefault()
        {
            var time = new FakeTimeProvider();
            var routing = Build((_, _) => Task.FromResult<ProviderRouteState?>(null), time);

            var state = await routing.GetAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.True(state.IsDefault);
            Assert.Equal(new[] { "aerodatabox" }, state.Providers);
            Assert.Null(state.ChangedBy);
        }

        [Fact]
        public async Task GetAsync_IsNotCached()
        {
            var time = new FakeTimeProvider();
            var reads = 0;
            var routing = Build((job, _) =>
            {
                reads++;
                return Task.FromResult<ProviderRouteState?>(
                    new ProviderRouteState(job, new[] { "aeroapi" }, false, null, null, null));
            }, time);

            await routing.GetAsync(ProviderJobs.FlightSearchRoute, default);
            await routing.GetAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(2, reads);
        }
    }
}
