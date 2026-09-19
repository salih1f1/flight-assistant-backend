using FlightAssistant.Application.Interfaces.Providers;
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
            var routing = Substitute.For<IProviderRouting>();
            var defaults = Substitute.For<IProviderRouteDefaults>();
            routing.GetAsync(ProviderJobs.FlightSearchRoute, Arg.Any<CancellationToken>())
                .Returns(new ProviderRouteState(ProviderJobs.FlightSearchRoute,
                    new[] { ProviderJobs.AeroDataBox }, false, DateTime.UtcNow, "Ada", "picked before rollback"));
            defaults.IsCeilinged(ProviderJobs.FlightSearchRoute).Returns(true);
            defaults.For(ProviderJobs.FlightSearchRoute).Returns(new[] { ProviderJobs.AeroApi });

            var handler = new GetProviderRoutesQueryHandler(routing, defaults);
            var rows = await handler.Handle(new GetProviderRoutesQuery(), default);

            Assert.Equal(new[] { ProviderJobs.AeroApi }, Assert.Single(rows).Providers);
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
            var routing = Substitute.For<IProviderRouting>();
            var defaults = Substitute.For<IProviderRouteDefaults>();
            routing.GetAsync(ProviderJobs.FlightSearchRoute, Arg.Any<CancellationToken>())
                .Returns(new ProviderRouteState(ProviderJobs.FlightSearchRoute,
                    new[] { ProviderJobs.AeroDataBox }, false, DateTime.UtcNow, "Ada", "chosen deliberately"));
            defaults.IsCeilinged(ProviderJobs.FlightSearchRoute).Returns(false);
            defaults.For(ProviderJobs.FlightSearchRoute).Returns(new[] { ProviderJobs.AeroApi });

            var handler = new GetProviderRoutesQueryHandler(routing, defaults);
            var rows = await handler.Handle(new GetProviderRoutesQuery(), default);

            Assert.Equal(new[] { ProviderJobs.AeroDataBox }, Assert.Single(rows).Providers);
        }
    }
}
