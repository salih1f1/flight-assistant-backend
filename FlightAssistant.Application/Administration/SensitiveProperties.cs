using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;

namespace FlightAssistant.Application.Administration
{
    /// <summary>
    /// The single registry of entity properties whose values must never be written verbatim into
    /// an audit row (Task 2's before/after snapshots), the admin data browser's display (A4), or
    /// anywhere else outside an explicit, audited reveal (A5). Do not scatter a second copy of
    /// this list anywhere — every future consumer, including A4's masking and A5's audited
    /// reveal, reads it from here.
    ///
    /// Property names are captured with <c>nameof</c> against the real entity types below, not as
    /// string literals — a renamed property fails the build here instead of silently protecting
    /// nothing. <c>SensitivePropertiesTests</c> additionally asserts, via reflection, that
    /// every registered pair actually resolves on its type.
    /// </summary>
    public static class SensitiveProperties
    {
        /// <summary>
        /// The one literal every masking consumer must render for a sensitive value — the audit
        /// interceptor's before/after snapshots, the admin data browser (A4), and the traveler
        /// detail drill-down (#322) all read this constant rather than each carrying their own
        /// copy of the string. This registry already owns *what* is sensitive; centralising *what
        /// masked looks like* here too closes the exact drift CLAUDE.md names for validator format
        /// rules — three independently-typed literals are one rename away from a masked value that
        /// no longer looks masked, which is worse than either an unmasked value (obviously wrong)
        /// or a consistently masked one.
        /// </summary>
        public const string RedactedPlaceholder = "<redacted>";

        private static readonly IReadOnlyDictionary<Type, IReadOnlySet<string>> Registry =
            new Dictionary<Type, IReadOnlySet<string>>
            {
                [typeof(Traveler)] = new HashSet<string>(StringComparer.Ordinal)
                {
                    nameof(Traveler.Email)
                },
                [typeof(DeviceToken)] = new HashSet<string>(StringComparer.Ordinal)
                {
                    nameof(DeviceToken.Token)
                },
                [typeof(TravelerLoyaltyMembership)] = new HashSet<string>(StringComparer.Ordinal)
                {
                    nameof(TravelerLoyaltyMembership.AccountNumber)
                }
            };

        private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Walks <paramref name="entityType"/> and its base types (exact type first), not just
        /// the exact type, when checking or collecting registrations below. A mapped derived
        /// entity type (EF TPH, e.g. a hypothetical <c>PremiumTraveler : Traveler</c> configured
        /// as its own entity type) reports its own exact <c>Type</c> as <c>Metadata.ClrType</c> —
        /// an exact-match-only lookup would miss the registration on <c>Traveler</c> entirely and
        /// let a subclass's sensitive properties through unmasked. This is deliberately the only
        /// direction this registry is forgiving in: under-masking leaks PII into an audit row or
        /// the admin panel, which cannot be undone once written; over-masking only redacts a value
        /// that turns out not to have needed it, which loses nothing but detail. That asymmetry —
        /// not symmetry, not performance — is why this walks up rather than matching exactly.
        /// </summary>
        private static IEnumerable<Type> TypeAndAncestors(Type entityType)
        {
            for (var type = entityType; type is not null; type = type.BaseType)
                yield return type;
        }

        /// <summary>
        /// Whether <paramref name="propertyName"/> on <paramref name="entityType"/> — or any base
        /// type it derives from — is registered as sensitive. The comparison is case-correct
        /// (ordinal): a property name that only differs by case from a registered one is not a
        /// match.
        /// </summary>
        public static bool IsSensitive(Type entityType, string propertyName) =>
            TypeAndAncestors(entityType).Any(t =>
                Registry.TryGetValue(t, out var properties) && properties.Contains(propertyName));

        /// <summary>
        /// All sensitive property names registered for <paramref name="entityType"/> or any base
        /// type it derives from, for a caller that needs to mask a whole record (A4) rather than
        /// test one property at a time. Returns an empty set for a type with no registered
        /// sensitive properties anywhere in its hierarchy — never null.
        /// </summary>
        public static IReadOnlySet<string> GetSensitiveProperties(Type entityType)
        {
            HashSet<string>? union = null;
            foreach (var type in TypeAndAncestors(entityType))
            {
                if (!Registry.TryGetValue(type, out var properties)) continue;
                union ??= new HashSet<string>(StringComparer.Ordinal);
                union.UnionWith(properties);
            }

            return union ?? Empty;
        }
    }
}
