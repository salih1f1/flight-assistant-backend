using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;
using FlightAssistant.Application.Verification;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.Providers;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.Providers
{
    /// <summary>
    /// The obligation every key in <see cref="ProviderJobs.All"/> places on the per-key lookups
    /// <c>GET</c>/<c>PUT /admin/providers</c> reach for it. <c>GetProviderRoutesQueryHandler</c> loops
    /// over that list calling <see cref="ProviderRouteDefaults.For"/>,
    /// <see cref="ProviderRouteDefaults.IsCeilinged"/>, <c>ProviderJobs.DisplayNameFor</c> and the
    /// options path (<c>OptionsFor</c>, or <c>EntityTypeFor</c> + <c>SupportedSourceNames</c>); the
    /// command handler additionally asks <c>ProviderJobs.TakesOrderedList</c>. Every one of them throws
    /// for a key its own switch expression does not name, so adding a key to <c>ProviderJobs.All</c>
    /// without extending all five turns <c>GET /admin/providers</c> into an unconditional 500 -- or
    /// makes every <c>PUT</c> to the new key one (#531 fix round 1, Finding 4; widened from two members
    /// to five by the whole-branch review, which found the other three uncovered).
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
        // IVerificationJobOptions is #531 task 7's new second dependency -- FakeJobOptions is the
        // same stand-in VerificationRequestRepository's own tests already use for "any direct
        // construction site needs one"; its DefaultSourcesFor mirrors the real
        // ourairports/aeroapi/mledoze configuration defaults, which is what keeps the obligation
        // test below (empty-list check) honest for the three verification keys.
        private static ProviderRouteDefaults Build(
            string? aeroDataBoxApiKey = "key", IVerificationJobOptions? verificationOptions = null) =>
            new(Options.Create(new AeroDataBoxOptions { ApiKey = aeroDataBoxApiKey }),
                verificationOptions ?? new FakeJobOptions());

        [Fact]
        public void EveryPerKeyLookup_AnswersForEveryKeyInProviderJobsAll()
        {
            var defaults = Build();
            // An empty registry is enough: the obligation is that the options path answers without
            // throwing for every key, and for a verification key that means EntityTypeFor resolving
            // at all. What SupportedSourceNames then finds depends on which sources are registered,
            // which is not this test's subject.
            var registry = Substitute.For<IVerificationSourceRegistry>();
            registry.Sources.Returns(Array.Empty<IVerificationSource>());

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

                // The handler renders this on every row, so an unnamed key 500s the whole endpoint
                // rather than degrading that one row.
                try
                {
                    var displayName = ProviderJobs.DisplayNameFor(job);
                    if (string.IsNullOrWhiteSpace(displayName))
                        failures.Add($"{job}: DisplayNameFor(...) returned a blank name.");
                }
                catch (Exception ex)
                {
                    failures.Add($"{job}: DisplayNameFor(...) threw {ex.GetType().Name}: {ex.Message}");
                }

                // The options path, exactly as GetProviderRoutesQueryHandler branches it: a static
                // list for flight-search-route, the registry for a verification key -- which needs
                // EntityTypeFor to resolve the key first, and that throws for a key it does not name.
                try
                {
                    _ = job == ProviderJobs.FlightSearchRoute
                        ? ProviderJobs.OptionsFor(job)
                        : ProviderJobs.SupportedSourceNames(registry, ProviderJobs.EntityTypeFor(job));
                }
                catch (Exception ex)
                {
                    failures.Add($"{job}: the options path threw {ex.GetType().Name}: {ex.Message}");
                }

                // SetProviderRouteCommandHandler asks this on every PUT, before anything else that
                // could refuse the request -- so an unnamed key makes every write to it a 500.
                try
                {
                    ProviderJobs.TakesOrderedList(job);
                }
                catch (Exception ex)
                {
                    failures.Add($"{job}: TakesOrderedList(...) threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                "A per-key lookup does not answer for every key in ProviderJobs.All -- " +
                "GET /admin/providers, or a PUT to that key, would 500: " +
                string.Join("; ", failures));
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
