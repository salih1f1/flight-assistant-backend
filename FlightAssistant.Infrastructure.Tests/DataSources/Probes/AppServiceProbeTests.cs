using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Hosting;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Infrastructure.DataSources.Probes;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.DataSources.Probes
{
    /// <summary>
    /// <see cref="AppServiceProbe"/> is bespoke state-to-outcome branching, like
    /// <see cref="AzureCostProbe"/> (see that class's own tests). This pins each
    /// <see cref="AppServiceState"/> value against the <see cref="DataSourceOutcome"/> the probe
    /// reports for it, and separately proves the constraint the whole feature rests on: a site fact
    /// (AlwaysOn, SiteState, ...) never influences the probe's answer, only whether the resource
    /// could be read at all.
    /// </summary>
    public class AppServiceProbeTests
    {
        private static AppServiceProbe Build(AppServiceSnapshot snapshot)
        {
            var provider = Substitute.For<IAppServiceProvider>();
            provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
            return new AppServiceProbe(provider);
        }

        private static AppServiceSnapshot Snapshot(AppServiceState state) => new(
            state, null, null, null, null, null, null, null);

        [Fact]
        public async Task Ok_MapsToOk()
        {
            var outcome = await Build(Snapshot(AppServiceState.Ok)).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.Ok, outcome);
        }

        [Fact]
        public async Task NotConfigured_MapsToNotConfigured()
        {
            var outcome = await Build(AppServiceSnapshot.NotConfigured()).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.NotConfigured, outcome);
        }

        [Fact]
        public async Task NotAuthorized_MapsToUnauthorized()
        {
            var outcome = await Build(AppServiceSnapshot.NotAuthorized()).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.Unauthorized, outcome);
        }

        [Fact]
        public async Task Unavailable_MapsToFailed()
        {
            var outcome = await Build(AppServiceSnapshot.Unavailable("Timed out.")).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.Failed, outcome);
        }

        // The constraint the whole feature rests on (task 3 brief): the probe reports on whether the
        // resource could be READ, never on what the answer was. A site with Always On off -- and,
        // for good measure, reported Stopped -- read successfully must still be Ok.
        [Fact]
        public async Task AlwaysOnFalse_AndAStoppedSite_StillMapToOk_NotAFailure()
        {
            var snapshot = new AppServiceSnapshot(
                AppServiceState.Ok, "site-a", "Stopped", "Normal", true, AlwaysOn: false, 0, null);

            var outcome = await Build(snapshot).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.Ok, outcome);
        }
    }
}
