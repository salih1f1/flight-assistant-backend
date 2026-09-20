using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Providers;
using FlightAssistant.Application.Queries.Admin.Providers;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Providers
{
    /// <summary>
    /// <see cref="GetProviderRoutesQueryHandler"/> (#531 fix round 1, Finding 2; extended by the
    /// whole-branch review). Pins both branches of the ceiling check -- which round 1 shipped with no
    /// dedicated test at all -- now as the <c>Providers</c>/<c>EffectiveProviders</c> split rather than
    /// one collapsed field, and pins that a switchable operation-map row publishes the live provider
    /// rather than its authored default. No test here cares about <c>Headroom</c> -- that is
    /// <c>GetProviderRoutesQueryHandlerHeadroomTests</c>' job -- so the six headroom-only dependencies
    /// below are stubbed just enough that the AeroAPI branch's own try/catch never has anything to catch.
    /// </summary>
    public class GetProviderRoutesQueryTests
    {
        private static readonly AeroApiAccountUsage EmptyUsage = new(
            TotalCalls: 0,
            TotalPages: 0,
            TotalCostUsd: 0m,
            TotalDiscountCostUsd: 0m,
            TotalSuccessfulCalls: 0,
            TotalFailedCalls: 0,
            ByOperation: Array.Empty<AeroApiOperationUsage>());

        private static GetProviderRoutesQueryHandler CreateHandler(
            IProviderRouting routing, IProviderRouteDefaults defaults, IVerificationSourceRegistry registry)
        {
            var budget = Substitute.For<IProviderBudgetGuard>();
            budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);

            var usage = Substitute.For<IAeroApiAccountUsageProvider>();
            usage.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(EmptyUsage);

            var cache = Substitute.For<ICacheService>();
            var budgetOptions = Substitute.For<IAeroApiBudgetOptions>();
            budgetOptions.MonthlyBudgetUsd.Returns(5m);
            var quota = Substitute.For<IProviderQuotaReader>();

            return new GetProviderRoutesQueryHandler(
                routing, defaults, registry, budget, usage, cache, budgetOptions, quota,
                new FakeTimeProvider(DateTimeOffset.UtcNow));
        }

        /// <summary>
        /// The documented rollback scenario these two tests exist for: an operator picked
        /// "aerodatabox" while it was reachable and the row persisted; <c>AeroDataBox:ApiKey</c> was
        /// then removed, so <c>IsCeilinged</c> flips to true and every real search now goes to AeroAPI
        /// regardless of what the stored row says.
        ///
        /// <para>
        /// Both halves are load-bearing, which is why this asserts on two fields rather than one.
        /// Publishing only the stored value would advertise "aerodatabox" while no search can reach
        /// it; publishing only the effective value -- this handler's shape before the whole-branch
        /// review -- put configuration's providers beside the stored row's <c>changedBy</c>,
        /// <c>changedUtc</c> and <c>reason</c> with <c>isDefault: false</c>, which reads as a named
        /// admin having chosen the provider configuration forced. <c>AdminJobDto</c> keeps
        /// <c>ConfigEnabled</c> and <c>SwitchEnabled</c> and derives <c>EffectiveEnabled</c> from
        /// them; this follows it.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Handle_KeyIsCeilinged_ReportsTheStoredRowAndTheEffectiveProvidersSeparately()
        {
            // ProviderJobs.All now carries the three verification keys alongside flight-search-route
            // (#531 task 7), so both substitutes below must answer for any key -- not only the one
            // this test cares about -- or GetProvidersRoutesQueryHandler's loop throws a null
            // reference the moment it reaches one of the other three.
            var routing = Substitute.For<IProviderRouting>();
            var defaults = Substitute.For<IProviderRouteDefaults>();
            var registry = Substitute.For<IVerificationSourceRegistry>();
            routing.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ProviderRouteState(ProviderJobs.FlightSearchRoute,
                    new[] { ProviderJobs.AeroDataBox }, false, DateTime.UtcNow, "Ada", "picked before rollback"));
            defaults.IsCeilinged(ProviderJobs.FlightSearchRoute).Returns(true);
            defaults.For(Arg.Any<string>()).Returns(new[] { ProviderJobs.AeroApi });
            // Task 9: the loop now asks the registry for the three verification keys' options too
            // (ProviderJobs.All carries all four since task 7) -- an empty registry is fine here,
            // since this test only cares about the flight-search-route row.
            registry.Sources.Returns(Array.Empty<IVerificationSource>());

            var handler = CreateHandler(routing, defaults, registry);
            var map = await handler.Handle(new GetProviderRoutesQuery(), default);

            var route = Assert.Single(map.Routes, r => r.Job == ProviderJobs.FlightSearchRoute);
            // What an operator chose, which is what changedBy/changedUtc/reason describe...
            Assert.Equal(new[] { ProviderJobs.AeroDataBox }, route.Providers);
            Assert.Equal("Ada", route.ChangedBy);
            Assert.False(route.IsDefault);
            // ...and what every search actually uses now that the environment has changed under it.
            Assert.Equal(new[] { ProviderJobs.AeroApi }, route.EffectiveProviders);
            Assert.False(route.CanChange);
        }

        /// <summary>
        /// Companion to the ceilinged case above, so both branches of the ternary are pinned rather
        /// than just one: with nothing ceilinging the key, the stored row IS the answer -- an
        /// operator's saved choice must not be silently overridden by the configured default when no
        /// environment fact forces it -- and the two fields then carry the same value, so a panel
        /// rendering either one is right.
        /// </summary>
        [Fact]
        public async Task Handle_KeyIsNotCeilinged_ReportsTheStoredRowInBothFields()
        {
            // Same reason as the ceilinged test above: answer for any key, then pick the
            // flight-search-route row out of all four by predicate.
            var routing = Substitute.For<IProviderRouting>();
            var defaults = Substitute.For<IProviderRouteDefaults>();
            var registry = Substitute.For<IVerificationSourceRegistry>();
            routing.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ProviderRouteState(ProviderJobs.FlightSearchRoute,
                    new[] { ProviderJobs.AeroDataBox }, false, DateTime.UtcNow, "Ada", "chosen deliberately"));
            defaults.IsCeilinged(ProviderJobs.FlightSearchRoute).Returns(false);
            defaults.For(Arg.Any<string>()).Returns(new[] { ProviderJobs.AeroApi });
            // Task 9: see the companion test above -- an empty registry is fine here too.
            registry.Sources.Returns(Array.Empty<IVerificationSource>());

            var handler = CreateHandler(routing, defaults, registry);
            var map = await handler.Handle(new GetProviderRoutesQuery(), default);

            var route = Assert.Single(map.Routes, r => r.Job == ProviderJobs.FlightSearchRoute);
            Assert.Equal(new[] { ProviderJobs.AeroDataBox }, route.Providers);
            Assert.Equal(new[] { ProviderJobs.AeroDataBox }, route.EffectiveProviders);
        }

        /// <summary>
        /// <c>ProviderOperationMap</c> authors each row's providers, and the route-search row is
        /// authored <c>["aerodatabox"]</c> -- so a flip to <c>aeroapi</c> left the map still claiming
        /// AeroDataBox serves route search, which is exactly what
        /// <c>ProviderOperationMapCompletenessTests</c> calls out: "a stale map is worse than no map:
        /// it states with apparent authority that a provider is not called". A switchable row's
        /// providers now come from that key's <c>EffectiveProviders</c>.
        /// </summary>
        [Fact]
        public async Task Handle_AStoredRowFlipsRouteSearch_TheMapRowReportsTheLiveProvider()
        {
            var routing = Substitute.For<IProviderRouting>();
            var defaults = Substitute.For<IProviderRouteDefaults>();
            var registry = Substitute.For<IVerificationSourceRegistry>();
            routing.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ProviderRouteState(ProviderJobs.FlightSearchRoute,
                    new[] { ProviderJobs.AeroApi }, false, DateTime.UtcNow, "Ada", "AeroDataBox rows looked wrong"));
            defaults.IsCeilinged(Arg.Any<string>()).Returns(false);
            defaults.For(Arg.Any<string>()).Returns(new[] { ProviderJobs.AeroDataBox });
            registry.Sources.Returns(Array.Empty<IVerificationSource>());

            var handler = CreateHandler(routing, defaults, registry);
            var map = await handler.Handle(new GetProviderRoutesQuery(), default);

            var operation = Assert.Single(map.Operations,
                o => o.RoutingKey == ProviderJobs.FlightSearchRoute);
            Assert.Equal(new[] { ProviderJobs.AeroApi }, operation.Providers);

            // A fixed row is untouched by the overlay: it keeps exactly what it was authored with.
            var flightNumberSearch = Assert.Single(map.Operations,
                o => o.Operation.Contains("anchored on a flight number"));
            Assert.Null(flightNumberSearch.RoutingKey);
            Assert.Equal(new[] { ProviderJobs.AeroApi }, flightNumberSearch.Providers);
        }
    }
}
