using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;
using FlightAssistant.Application.Queries.Admin.Providers;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Providers
{
    /// <summary>
    /// <see cref="GetProviderRoutesQueryHandler"/> (#531 fix round 1, Finding 2). Pins both branches
    /// of the ceiling check, which the fix shipped in round 1 with no dedicated test at all.
    /// </summary>
    public class GetProviderRoutesQueryTests
    {
        /// <summary>
        /// The documented rollback scenario this fix exists for: an operator picked "aerodatabox"
        /// while it was reachable and the row persisted; <c>AeroDataBox:ApiKey</c> was then removed,
        /// so <c>IsCeilinged</c> flips to true and every real search now goes to AeroAPI regardless
        /// of what the stored row says. Reporting the stored value here would have this endpoint
        /// publish "aerodatabox" while no search can actually reach it -- this pins that a ceilinged
        /// key reports the EFFECTIVE provider (the configured default) instead.
        /// </summary>
        [Fact]
        public async Task Handle_KeyIsCeilinged_ReportsTheEffectiveProvider_NotTheStoredRow()
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

            var handler = new GetProviderRoutesQueryHandler(routing, defaults, registry);
            var rows = await handler.Handle(new GetProviderRoutesQuery(), default);

            var route = Assert.Single(rows, r => r.Job == ProviderJobs.FlightSearchRoute);
            Assert.Equal(new[] { ProviderJobs.AeroApi }, route.Providers);
        }

        /// <summary>
        /// Companion to the ceilinged case above, so both branches of the ternary are pinned rather
        /// than just one: with nothing ceilinging the key, the stored row IS the answer -- an
        /// operator's saved choice must not be silently overridden by the configured default when no
        /// environment fact forces it.
        /// </summary>
        [Fact]
        public async Task Handle_KeyIsNotCeilinged_ReportsTheStoredRow()
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

            var handler = new GetProviderRoutesQueryHandler(routing, defaults, registry);
            var rows = await handler.Handle(new GetProviderRoutesQuery(), default);

            var route = Assert.Single(rows, r => r.Job == ProviderJobs.FlightSearchRoute);
            Assert.Equal(new[] { ProviderJobs.AeroDataBox }, route.Providers);
        }
    }
}
