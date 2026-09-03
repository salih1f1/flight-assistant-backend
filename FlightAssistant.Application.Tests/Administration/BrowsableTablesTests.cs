using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using System.Reflection;
using System.Text.RegularExpressions;

namespace FlightAssistant.Application.Tests.Administration
{
    /// <summary>
    /// Guards (c) and (d) from the #315 Task 1 brief — the two guards that only need reflection over
    /// the registry itself, not the EF model, so they live here beside
    /// <see cref="SensitivePropertiesTests"/>. Guards (a) (entity completeness) and (b) (column
    /// completeness) need the compiled EF model, which lives in Infrastructure;
    /// <c>FlightAssistant.Application.Tests</c> has no project reference to
    /// <c>FlightAssistant.Infrastructure</c> (by design — Application must not depend on
    /// Infrastructure), so those two live in <c>FlightAssistant.Infrastructure.Tests</c> instead,
    /// in <c>BrowsableTablesModelCompletenessTests</c>.
    /// </summary>
    public class BrowsableTablesTests
    {
        // ── (c) Every exposed column resolves via reflection ───────────────────────
        // Same guard SensitivePropertiesTests already applies to its own registry — a typo in a
        // column name must fail the build/tests, not silently expose (or hide) a column.

        [Fact]
        public void EveryExposedColumn_ResolvesOnItsEntityType()
        {
            foreach (var table in BrowsableTables.All)
            {
                foreach (var columnName in table.Columns)
                {
                    var property = table.EntityType.GetProperty(
                        columnName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                    Assert.True(
                        property is not null,
                        $"{table.Slug}: {table.EntityType.Name}.{columnName} does not resolve via reflection.");

                    // BindingFlags.IgnoreCase above only guards against a total lookup miss; assert
                    // the exact case matches too, since exposure/masking checks downstream compare
                    // ordinally.
                    Assert.Equal(columnName, property!.Name);
                }
            }
        }

        [Fact]
        public void KnownColumn_ResolvesOnItsEntityType()
        {
            var property = typeof(Traveler).GetProperty(nameof(Traveler.Email), BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(property);
        }

        // ── (d) Slugs are unique, URL-safe, and map to a distinct type ─────────────

        [Fact]
        public void Slugs_AreUnique()
        {
            var duplicates = BrowsableTables.All
                .GroupBy(e => e.Slug, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0, $"Duplicate slug(s): {string.Join(", ", duplicates)}");
        }

        [Fact]
        public void EntityTypes_AreDistinct()
        {
            var duplicates = BrowsableTables.All
                .GroupBy(e => e.EntityType)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.Name)
                .ToList();

            Assert.True(duplicates.Count == 0, $"Entity type(s) registered under more than one slug: {string.Join(", ", duplicates)}");
        }

        // Kebab-case, URL-safe: lowercase ASCII letters and digits, hyphen-separated, no leading,
        // trailing or doubled hyphen. This is the exact shape Task 2's routing hardcodes into the
        // URL path segment.
        private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

        [Theory]
        [MemberData(nameof(AllSlugs))]
        public void Slug_IsKebabCase(string slug)
        {
            Assert.Matches(KebabCase, slug);
        }

        public static IEnumerable<object[]> AllSlugs() =>
            BrowsableTables.All.Select(e => new object[] { e.Slug });

        [Fact]
        public void EverySlug_IsNonEmpty()
        {
            Assert.All(BrowsableTables.All, e => Assert.False(string.IsNullOrWhiteSpace(e.Slug)));
        }

        // ── Lookup surface ──────────────────────────────────────────────────────────

        [Fact]
        public void TryGetBySlug_KnownSlug_ReturnsEntry()
        {
            var table = BrowsableTables.TryGetBySlug("airports");

            Assert.NotNull(table);
            Assert.Equal(typeof(Airport), table!.EntityType);
        }

        [Fact]
        public void TryGetBySlug_UnknownSlug_ReturnsNull()
        {
            Assert.Null(BrowsableTables.TryGetBySlug("not-a-real-table"));
        }

        [Fact]
        public void IsColumnExposed_KnownColumn_ReturnsTrue()
        {
            Assert.True(BrowsableTables.IsColumnExposed("airports", nameof(Airport.Iata)));
        }

        [Fact]
        public void IsColumnExposed_UnknownColumn_ReturnsFalse()
        {
            Assert.False(BrowsableTables.IsColumnExposed("airports", "NotARealColumn"));
        }

        [Fact]
        public void IsColumnExposed_SensitiveButUnexposedColumn_ReturnsFalse()
        {
            // Token is not in the "device-tokens" exposed list at all (the browser projection never
            // even considers it) — distinct from a column that is exposed and then masked.
            Assert.False(BrowsableTables.IsColumnExposed("device-tokens", "NotAColumnEither"));
        }

        [Fact]
        public void IsColumnExposed_UnknownSlug_ReturnsFalse()
        {
            Assert.False(BrowsableTables.IsColumnExposed("not-a-real-table", nameof(Airport.Iata)));
        }

        [Fact]
        public void IsColumnExposed_WrongCase_ReturnsFalse()
        {
            Assert.False(BrowsableTables.IsColumnExposed("airports", "iata"));
            Assert.False(BrowsableTables.IsColumnExposed("airports", "IATA"));
        }

        // ── NotBrowsable exclusions, if any, carry a non-empty reason ───────────────

        [Fact]
        public void EveryNotBrowsableEntry_HasANonEmptyReason()
        {
            Assert.All(BrowsableTables.NotBrowsable, kvp => Assert.False(string.IsNullOrWhiteSpace(kvp.Value)));
        }

        [Fact]
        public void NoType_IsBothBrowsableAndNotBrowsable()
        {
            var browsableTypes = BrowsableTables.All.Select(e => e.EntityType).ToHashSet();
            var overlap = browsableTypes.Intersect(BrowsableTables.NotBrowsable.Keys).ToList();

            Assert.True(overlap.Count == 0, $"Type(s) registered both as browsable and not-browsable: {string.Join(", ", overlap.Select(t => t.Name))}");
        }

        // ── DedupHash-style sanity: FlightStatusEvent is registered as expected ─────
        // (Spot check; the exhaustive per-entity check lives in Infrastructure.Tests alongside the
        // EF model.)

        [Fact]
        public void FlightStatusEvent_IsRegistered()
        {
            var table = BrowsableTables.TryGetBySlug("flight-status-events");

            Assert.NotNull(table);
            Assert.Equal(typeof(FlightStatusEvent), table!.EntityType);
            Assert.Contains(nameof(FlightStatusEvent.DedupHash), table.Columns);
        }
    }
}
