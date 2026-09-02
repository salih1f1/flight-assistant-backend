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
        /// Whether <paramref name="propertyName"/> on <paramref name="entityType"/> is
        /// registered as sensitive. The comparison is case-correct (ordinal): a property name
        /// that only differs by case from a registered one is not a match.
        /// </summary>
        public static bool IsSensitive(Type entityType, string propertyName) =>
            Registry.TryGetValue(entityType, out var properties) && properties.Contains(propertyName);

        /// <summary>
        /// All sensitive property names registered for <paramref name="entityType"/>, for a
        /// caller that needs to mask a whole record (A4) rather than test one property at a time.
        /// Returns an empty set for a type with no registered sensitive properties — never null.
        /// </summary>
        public static IReadOnlySet<string> GetSensitiveProperties(Type entityType) =>
            Registry.TryGetValue(entityType, out var properties) ? properties : Empty;
    }
}
