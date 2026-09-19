using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.Providers
{
    /// <summary>
    /// The obligation <c>GetProviderRoutesQueryHandler</c> places on <see cref="ProviderRouteDefaults"/>:
    /// it calls both <c>For</c> and <c>IsCeilinged</c> for EVERY key in <see cref="ProviderJobs.All"/>,
    /// and each throws <see cref="InvalidOperationException"/> for any key its own switch expression
    /// does not name. Adding a key to <c>ProviderJobs.All</c> without also extending
    /// <see cref="ProviderRouteDefaults"/> turns <c>GET /admin/providers</c> into an unconditional 500
    /// (#531 fix round 1, Finding 4) -- the next task in this plan does exactly that, adding three
    /// verification keys to <c>ProviderJobs.All</c>.
    ///
    /// <para>
    /// Driven from <see cref="ProviderJobs.All"/> itself, never a hardcoded list of keys copied from
    /// it: a hardcoded copy would need updating in lockstep with the very registry this test exists to
    /// check, which is exactly the drift it is meant to catch. Proven by mutation, not reasoning:
    /// temporarily adding a fourth key (<c>"bogus-job"</c>) to <c>ProviderJobs.All</c> with no matching
    /// arm in <see cref="ProviderRouteDefaults.For"/>/<see cref="ProviderRouteDefaults.IsCeilinged"/>
    /// turned this test red, naming <c>bogus-job</c> and the <see cref="InvalidOperationException"/>
    /// it threw -- then reverted. See the fix-round report for the transcript.
    /// </para>
    /// </summary>
    public class ProviderRouteDefaultsTests
    {
        private static ProviderRouteDefaults Build(string? aeroDataBoxApiKey = "key") =>
            new(Options.Create(new AeroDataBoxOptions { ApiKey = aeroDataBoxApiKey }));

        [Fact]
        public void ForAndIsCeilinged_AnswerForEveryKeyInProviderJobsAll()
        {
            var defaults = Build();
            var failures = new List<string>();

            foreach (var job in ProviderJobs.All)
            {
                IReadOnlyList<string>? forResult = null;
                try
                {
                    forResult = defaults.For(job);
                }
                catch (Exception ex)
                {
                    failures.Add($"{job}: For(...) threw {ex.GetType().Name}: {ex.Message}");
                }

                if (forResult is not null && forResult.Count == 0)
                    failures.Add($"{job}: For(...) returned an empty list.");

                try
                {
                    defaults.IsCeilinged(job);
                }
                catch (Exception ex)
                {
                    failures.Add($"{job}: IsCeilinged(...) threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                "ProviderRouteDefaults does not answer for every key in ProviderJobs.All -- " +
                $"GET /admin/providers would 500 for at least one of them: {string.Join("; ", failures)}");
        }

        // #531 fix round 2, GAP 2: the obligation test above proves For/IsCeilinged answer for
        // every key without throwing, but pins nothing about what they return. That matters because
        // the "is AeroDataBox reachable" rule is now duplicated in two places --
        // FlightSearchRouter.ResolveAsync has its own `!_aeroDataBoxOptions.Value.IsConfigured`
        // check, under its own test coverage -- and this copy, in ProviderRouteDefaults, had none.
        // The two could diverge silently: one accepting a change the other ignores, or refusing one
        // the other would honour.

        [Fact]
        public void IsCeilingedAndFor_WhenAeroDataBoxApiKeyIsUnset_CeilingedIsTrue_AndForReturnsAeroApi()
        {
            var defaults = Build(aeroDataBoxApiKey: null);

            Assert.True(defaults.IsCeilinged(ProviderJobs.FlightSearchRoute));
            Assert.Equal(new[] { ProviderJobs.AeroApi }, defaults.For(ProviderJobs.FlightSearchRoute));
        }

        [Fact]
        public void IsCeilingedAndFor_WhenAeroDataBoxApiKeyIsSet_CeilingedIsFalse_AndForReturnsAeroDataBox()
        {
            var defaults = Build(aeroDataBoxApiKey: "key");

            Assert.False(defaults.IsCeilinged(ProviderJobs.FlightSearchRoute));
            Assert.Equal(new[] { ProviderJobs.AeroDataBox }, defaults.For(ProviderJobs.FlightSearchRoute));
        }
    }
}
