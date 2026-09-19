using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Providers;
using FlightAssistant.Application.Queries.Admin.Providers;
using FlightAssistant.Application.Queries.Admin.Usage;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlightAssistant.Application.Tests.Queries.Admin.Providers
{
    /// <summary>
    /// <see cref="GetProviderRoutesQueryHandler"/>'s headroom block (#531 task 12). Two claims carry
    /// the risk here, not the DTO mapping: that the AeroAPI half genuinely shares the usage gauge's
    /// cached upstream read rather than coincidentally landing on the same numbers, and that a failed
    /// read degrades the map to nulls instead of turning one provider's outage into a 500 for the
    /// whole endpoint. The AeroDataBox half only ever relays <see cref="IProviderQuotaReader"/>'s last
    /// reading, so its tests are a straight mapping check.
    /// </summary>
    public class GetProviderRoutesQueryHandlerHeadroomTests
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 3, 0, TimeSpan.Zero);

        /// <summary>Real dictionary-backed cache, so a handler that resolved a different range, or
        /// built a differently-shaped key, than <c>GetAeroApiUsageQueryHandler</c>/
        /// <c>GetFlightDataUsageQueryHandler</c> would miss rather than silently "hit" -- the whole
        /// point of the shared entry. Mirrors <c>GetFlightDataUsageQueryTests.FakeCache</c>.</summary>
        private sealed class FakeCache : ICacheService
        {
            private readonly Dictionary<string, object?> _entries = [];

            public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
                Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);

            public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
            {
                _entries[key] = value;
                return Task.CompletedTask;
            }

            public Task RemoveAsync(string key, CancellationToken ct = default)
            {
                _entries.Remove(key);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeBudgetOptions : IAeroApiBudgetOptions
        {
            public decimal MonthlyBudgetUsd => 5m;
            public decimal SoftLimitRatio => 0.8m;
        }

        private readonly IProviderRouting _routing = Substitute.For<IProviderRouting>();
        private readonly IProviderRouteDefaults _defaults = Substitute.For<IProviderRouteDefaults>();
        private readonly IVerificationSourceRegistry _registry = Substitute.For<IVerificationSourceRegistry>();
        private readonly IProviderBudgetGuard _budget = Substitute.For<IProviderBudgetGuard>();
        private readonly IAeroApiAccountUsageProvider _usage = Substitute.For<IAeroApiAccountUsageProvider>();
        private readonly FakeCache _cache = new();
        private readonly FakeBudgetOptions _budgetOptions = new();
        private readonly IProviderQuotaReader _quota = Substitute.For<IProviderQuotaReader>();
        private readonly FakeTimeProvider _clock = new(Now);

        public GetProviderRoutesQueryHandlerHeadroomTests()
        {
            // The route-building loop's own reads, answered generically so every test in this class
            // can focus on the headroom block alone rather than also standing up per-key ceiling data.
            _routing.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci => new ProviderRouteState((string)ci[0], Array.Empty<string>(), true, null, null, null));
            _defaults.For(Arg.Any<string>()).Returns(Array.Empty<string>());
            _registry.Sources.Returns(Array.Empty<IVerificationSource>());
        }

        private GetProviderRoutesQueryHandler CreateHandler() =>
            new(_routing, _defaults, _registry, _budget, _usage, _cache, _budgetOptions, _quota, _clock);

        [Fact]
        public async Task AeroApiHeadroom_SharesTheUsageGaugesCacheEntry_AndMakesNoCallOfItsOwnWhenItIsWarm()
        {
            // Warms the cache through the REAL gauge handler -- GetAeroApiUsageQueryHandler, exactly
            // as GET /admin/usage/aeroapi would -- rather than reaching for UsageRange/AeroApiUsageCache
            // directly: both are internal to FlightAssistant.Application, invisible from this test
            // assembly, and going around them by hand would prove only that this test can build the
            // same key, not that the production handler does. A different range resolution, or a
            // differently-shaped key, would leave this cache entry unmatched and the assertions below
            // would see the handler's own (unconfigured) _usage substitute instead.
            var warmingProvider = Substitute.For<IAeroApiAccountUsageProvider>();
            warmingProvider
                .GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(new AeroApiAccountUsage(10, 12, 3.25m, 0m, 10, 0, Array.Empty<AeroApiOperationUsage>()));
            var gauge = new GetAeroApiUsageQueryHandler(
                warmingProvider, _cache, _budgetOptions, Substitute.For<IAeroApiThrottleObserver>(), _clock);
            await gauge.Handle(new GetAeroApiUsageQuery(), default);

            _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.SoftLimit);

            var map = await CreateHandler().Handle(new GetProviderRoutesQuery(), default);

            var aeroApi = Assert.Single(map.Headroom, h => h.Provider == ProviderJobs.AeroApi);
            Assert.Equal("SoftLimit", aeroApi.BudgetState);
            Assert.Equal(3.25m, aeroApi.SpentUsd);
            Assert.Equal(5m, aeroApi.BudgetUsd);

            // Never called: the answer came from the entry the gauge populated, proving a shared
            // cache read rather than a coincidence -- this handler's own usage provider is never
            // configured to return anything, so a miss here would have surfaced as null spend.
            await _usage.DidNotReceive().GetUsageAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task AeroApiHeadroom_DegradesToNulls_WhenTheUsageReadFails()
        {
            _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
            _usage.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("AeroAPI unreachable"));

            // Must not throw: one provider's outage blanking two numbers on the map is the point of
            // the try/catch, not a 500 for the whole endpoint (CLAUDE.md's "cannot tell is not nothing
            // spent" rule, restated for this route).
            var map = await CreateHandler().Handle(new GetProviderRoutesQuery(), default);

            var aeroApi = Assert.Single(map.Headroom, h => h.Provider == ProviderJobs.AeroApi);
            Assert.Null(aeroApi.BudgetState);
            Assert.Null(aeroApi.SpentUsd);
            // Still known -- it's local configuration, not a fact the failed call owns.
            Assert.Equal(5m, aeroApi.BudgetUsd);
            Assert.Null(aeroApi.QuotaRemaining);
            Assert.Null(aeroApi.QuotaLimit);
            Assert.Null(aeroApi.ObservedUtc);
        }

        [Fact]
        public async Task AeroDataBoxHeadroom_ReflectsTheLastObservedQuota_AndCarriesNoBudgetFields()
        {
            var observedUtc = Now.AddMinutes(-2);
            _quota.AeroDataBox.Returns(new ProviderQuota(Remaining: 700, Limit: 1505, ObservedUtc: observedUtc));
            _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
            _usage.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(new AeroApiAccountUsage(0, 0, 0m, 0m, 0, 0, Array.Empty<AeroApiOperationUsage>()));

            var map = await CreateHandler().Handle(new GetProviderRoutesQuery(), default);

            var aeroDataBox = Assert.Single(map.Headroom, h => h.Provider == ProviderJobs.AeroDataBox);
            Assert.Equal(700, aeroDataBox.QuotaRemaining);
            Assert.Equal(1505, aeroDataBox.QuotaLimit);
            Assert.Equal(observedUtc, aeroDataBox.ObservedUtc);
            Assert.Null(aeroDataBox.BudgetState);
            Assert.Null(aeroDataBox.SpentUsd);
            Assert.Null(aeroDataBox.BudgetUsd);
        }

        [Fact]
        public async Task AeroDataBoxHeadroom_IsNull_WhenThisProcessHasNeverCalledIt()
        {
            // IProviderQuotaReader.AeroDataBox defaults to null on the substitute -- "no call yet",
            // which must render as unknown, never as zero remaining.
            var map = await CreateHandler().Handle(new GetProviderRoutesQuery(), default);

            var aeroDataBox = Assert.Single(map.Headroom, h => h.Provider == ProviderJobs.AeroDataBox);
            Assert.Null(aeroDataBox.QuotaRemaining);
            Assert.Null(aeroDataBox.QuotaLimit);
            Assert.Null(aeroDataBox.ObservedUtc);
        }
    }
}
