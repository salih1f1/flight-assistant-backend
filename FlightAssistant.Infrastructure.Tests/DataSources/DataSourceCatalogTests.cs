using FlightAssistant.Application.DataSources;
using FlightAssistant.Infrastructure.DataSources;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.DataSources
{
    public class DataSourceCatalogTests
    {
        private static readonly DataSourceCatalog Catalog = new();

        [Fact]
        public void EveryKeyIsUnique()
        {
            var keys = Catalog.Sources.Select(s => s.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void EverySourceExplainsWhatIsChecked()
        {
            Assert.All(Catalog.Sources, s => Assert.False(string.IsNullOrWhiteSpace(s.WhatIsChecked)));
        }

        /// <summary>
        /// The test that earns its keep. Six sources were invisible on the health page because
        /// nothing forced a new integration to appear there. This fails the build when a named
        /// HttpClient is registered with no catalog entry, so source #12 cannot repeat the story.
        /// </summary>
        [Fact]
        public void EveryNamedHttpClientHasACatalogEntry()
        {
            var registered = NamedHttpClients.All;
            var catalogued = Catalog.Sources
                .Where(s => s.HttpClientName is not null)
                .Select(s => s.HttpClientName!)
                .ToHashSet(StringComparer.Ordinal);

            var missing = registered.Where(name => !catalogued.Contains(name)).ToList();

            Assert.True(
                missing.Count == 0,
                $"These named HttpClients have no DataSourceCatalog entry, so they would be " +
                $"invisible on the health page: {string.Join(", ", missing)}");
        }

        [Fact]
        public void AProbeThatCostsMoneySaysSo()
        {
            foreach (var source in Catalog.Sources.Where(s => s.SupportsProbe && s.ProbeCostNote is not null))
                Assert.False(string.IsNullOrWhiteSpace(source.ProbeCostNote));
        }

        [Fact]
        public void TryGet_ReturnsNull_ForAnUnknownKey()
        {
            Assert.Null(Catalog.TryGet("not-a-source"));
        }
    }
}
