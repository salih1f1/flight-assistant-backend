using System.Reflection;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;

namespace FlightAssistant.Infrastructure.Tests.Providers
{
    /// <summary>
    /// Keeps <see cref="ProviderOperationMap"/> honest. An authored map rots, and a stale map is
    /// worse than no map: it states with apparent authority that a provider is not called.
    ///
    /// <para>
    /// <b>The obligation rule is structural, not incidental.</b> A type implementing one of the
    /// provider interfaces that does <i>not</i> take one of those interfaces as a constructor
    /// parameter is a real provider — decorators, selectors and caches wrap one, real providers do
    /// not — and every real provider owes at least one map row. Mutation-tested both ways when it
    /// was written: deleting a row turns it red, and adding a provider turns it red.
    /// </para>
    /// </summary>
    public class ProviderOperationMapCompletenessTests
    {
        private static readonly Type[] ProviderInterfaces =
        [
            typeof(IFlightSearchProvider),
            typeof(IFlightStatusProvider),
            typeof(IVerificationSource),
        ];

        [Fact]
        public void EveryRealProviderAppearsOnTheMap()
        {
            var named = ProviderOperationMap.Entries
                .SelectMany(e => e.Providers)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();

            foreach (var type in RealProviders())
            {
                // A provider's map name is the catalog/source name it is known by, which is not
                // derivable from its type name -- so match on the type's own name containing a named
                // provider, which is how every provider in this codebase is actually named
                // (AeroApiFlightStatusProvider -> aeroapi, MledozeCountriesVerificationSource ->
                // mledoze, OurAirportsVerificationSource -> ourairports).
                if (!named.Any(name => type.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    missing.Add(type.Name);
            }

            Assert.True(missing.Count == 0,
                "These providers appear on no ProviderOperationMap row: " + string.Join(", ", missing) +
                ". Add a row, or -- if the provider genuinely serves no operation -- say so in its row's FixedReason.");
        }

        /// <summary>
        /// Concrete provider implementations, excluding every wrapper. A wrapper is identified by
        /// taking one of the provider interfaces as a constructor parameter, which is what a
        /// decorator, selector or caching layer does and a real provider never does.
        /// </summary>
        private static IEnumerable<Type> RealProviders()
        {
            var assembly = typeof(FlightAssistant.Infrastructure.DependencyInjection).Assembly;

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!ProviderInterfaces.Any(i => i.IsAssignableFrom(type))) continue;

                var wrapsAProvider = type.GetConstructors()
                    .SelectMany(c => c.GetParameters())
                    .Any(p => ProviderInterfaces.Any(i => i.IsAssignableFrom(p.ParameterType)));

                if (!wrapsAProvider) yield return type;
            }
        }

        [Fact]
        public void EverySwitchableRowNamesAKnownRoutingKey()
        {
            foreach (var entry in ProviderOperationMap.Entries.Where(e => e.RoutingKey is not null))
                Assert.Contains(entry.RoutingKey!, ProviderJobs.All);
        }

        [Fact]
        public void EveryRowIsEitherSwitchableOrExplainsWhyNot()
        {
            // A row with neither a routing key nor a reason is the failure mode this map exists to
            // prevent: a provider call nobody can change and nobody has justified.
            foreach (var entry in ProviderOperationMap.Entries)
            {
                Assert.True(
                    entry.RoutingKey is not null || !string.IsNullOrWhiteSpace(entry.FixedReason),
                    $"'{entry.Operation}' is neither switchable nor explained.");
            }
        }

        [Fact]
        public void EveryRoutingKeyAppearsOnExactlyOneRow()
        {
            foreach (var job in ProviderJobs.All)
                Assert.Single(ProviderOperationMap.Entries, e => e.RoutingKey == job);
        }

        [Fact]
        public void TheRouteSearchRowWarnsThatRoutingToAeroApiReintroducesTheBudgetRefusal()
        {
            // Spec section 6.1. This is the one consequence of a flip that an operator cannot infer
            // from the row itself, and it bites precisely when they would reach for the switch, so it
            // is asserted rather than left to survive a future edit of the prose.
            var row = Assert.Single(ProviderOperationMap.Entries,
                e => e.RoutingKey == ProviderJobs.FlightSearchRoute);

            Assert.NotNull(row.Note);
            Assert.Contains("SoftLimit", row.Note!);
        }
    }
}
