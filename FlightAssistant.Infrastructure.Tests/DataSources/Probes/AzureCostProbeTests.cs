using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Usage;
using FlightAssistant.Infrastructure.DataSources.Probes;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.DataSources.Probes
{
    /// <summary>
    /// <see cref="AzureCostProbe"/> is bespoke state-to-outcome branching (#496 task 3, final
    /// review) -- unlike every HTTP-status-mapping probe in <c>HttpSourceProbes.cs</c>, though
    /// <see cref="AppServiceProbe"/> (see its own tests) is now the same shape for the same reason.
    /// This pins each of the five <see cref="AzureBudgetState"/> values against the
    /// <see cref="DataSourceOutcome"/> the probe reports for it.
    /// </summary>
    public class AzureCostProbeTests
    {
        private static AzureCostProbe Build(AzureBudgetSnapshot snapshot)
        {
            var provider = Substitute.For<IAzureCostProvider>();
            provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
            return new AzureCostProbe(provider);
        }

        private static AzureBudgetSnapshot Snapshot(AzureBudgetState state) => new(
            state, null, null, null, null, null, null, null, null, null);

        [Fact]
        public async Task Ok_MapsToOk()
        {
            var outcome = await Build(Snapshot(AzureBudgetState.Ok)).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.Ok, outcome);
        }

        // NoBudget still answered from Cost Management with a clean response, which already proves
        // the role is granted -- the question this probe exists to answer -- so it counts as Ok
        // here, the same way the dashboard card does not treat "no budget defined" as a fault.
        [Fact]
        public async Task NoBudget_MapsToOk()
        {
            var outcome = await Build(Snapshot(AzureBudgetState.NoBudget)).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.Ok, outcome);
        }

        [Fact]
        public async Task NotConfigured_MapsToNotConfigured()
        {
            var outcome = await Build(AzureBudgetSnapshot.NotConfigured()).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.NotConfigured, outcome);
        }

        [Fact]
        public async Task NotAuthorized_MapsToUnauthorized()
        {
            var outcome = await Build(AzureBudgetSnapshot.NotAuthorized()).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.Unauthorized, outcome);
        }

        // ACCEPTED DEVIATION (see the comment on AzureCostProbe.ProbeAsync itself): Unavailable is
        // the only state the switch's `_` arm actually reaches today, including a 429, which maps
        // to Failed here even though DataSourceObservingHandler records RateLimited for the same
        // underlying HTTP response on the "AzureCost" client. Not fixed -- see that comment for why.
        [Fact]
        public async Task Unavailable_MapsToFailed()
        {
            var outcome = await Build(AzureBudgetSnapshot.Unavailable("Timed out.")).ProbeAsync(CancellationToken.None);

            Assert.Equal(DataSourceOutcome.Failed, outcome);
        }
    }
}
