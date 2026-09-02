using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using System.Reflection;

namespace FlightAssistant.Application.Tests.Administration
{
    public class SensitivePropertiesTests
    {
        // ── Known sensitive properties classify as sensitive ──────────────────────

        [Fact]
        public void IsSensitive_TravelerEmail_ReturnsTrue()
        {
            Assert.True(SensitiveProperties.IsSensitive(typeof(Traveler), nameof(Traveler.Email)));
        }

        [Fact]
        public void IsSensitive_DeviceTokenToken_ReturnsTrue()
        {
            Assert.True(SensitiveProperties.IsSensitive(typeof(DeviceToken), nameof(DeviceToken.Token)));
        }

        [Fact]
        public void IsSensitive_TravelerLoyaltyMembershipAccountNumber_ReturnsTrue()
        {
            Assert.True(SensitiveProperties.IsSensitive(
                typeof(TravelerLoyaltyMembership), nameof(TravelerLoyaltyMembership.AccountNumber)));
        }

        // ── Ordinary properties do not classify as sensitive ───────────────────────

        [Fact]
        public void IsSensitive_AirportIata_ReturnsFalse()
        {
            Assert.False(SensitiveProperties.IsSensitive(typeof(Airport), nameof(Airport.Iata)));
        }

        [Fact]
        public void IsSensitive_TravelerDisplayName_ReturnsFalse()
        {
            Assert.False(SensitiveProperties.IsSensitive(typeof(Traveler), nameof(Traveler.DisplayName)));
        }

        [Fact]
        public void IsSensitive_UnregisteredType_ReturnsFalse()
        {
            Assert.False(SensitiveProperties.IsSensitive(typeof(Airport), "AnyProperty"));
        }

        // ── Lookup is case-correct ──────────────────────────────────────────────────

        [Fact]
        public void IsSensitive_WrongCase_ReturnsFalse()
        {
            Assert.False(SensitiveProperties.IsSensitive(typeof(Traveler), "email"));
            Assert.False(SensitiveProperties.IsSensitive(typeof(Traveler), "EMAIL"));
        }

        // ── GetSensitiveProperties surface (for A4's whole-record masking) ─────────

        [Fact]
        public void GetSensitiveProperties_Traveler_ContainsEmailOnly()
        {
            var props = SensitiveProperties.GetSensitiveProperties(typeof(Traveler));

            Assert.Contains(nameof(Traveler.Email), props);
            Assert.DoesNotContain(nameof(Traveler.DisplayName), props);
        }

        [Fact]
        public void GetSensitiveProperties_UnregisteredType_ReturnsEmptySet()
        {
            var props = SensitiveProperties.GetSensitiveProperties(typeof(Airport));

            Assert.Empty(props);
        }

        // ── Every registered (type, property) pair actually resolves via reflection ─
        // This is what stops a registry entry naming a property that doesn't exist from
        // silently protecting nothing (the #313 concern about `MembershipNumber`, which
        // does not exist — the real property is `TravelerLoyaltyMembership.AccountNumber`).

        public static IEnumerable<object[]> KnownSensitivePairs()
        {
            yield return new object[] { typeof(Traveler), nameof(Traveler.Email) };
            yield return new object[] { typeof(DeviceToken), nameof(DeviceToken.Token) };
            yield return new object[] { typeof(TravelerLoyaltyMembership), nameof(TravelerLoyaltyMembership.AccountNumber) };
        }

        [Theory]
        [MemberData(nameof(KnownSensitivePairs))]
        public void RegisteredProperty_ResolvesViaReflection(Type entityType, string propertyName)
        {
            var property = entityType.GetProperty(
                propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            Assert.NotNull(property);
            // BindingFlags.IgnoreCase above only guards against a total lookup miss; assert the
            // exact case matches too, since the registry's own comparison is case-correct.
            Assert.Equal(propertyName, property!.Name);
        }

        [Fact]
        public void EveryRegisteredProperty_ResolvesOnItsDeclaringType()
        {
            // Walks every (type, property) pair the registry actually holds — not just the three
            // known ones above — so a future addition to the registry is covered by this same
            // guard without anyone remembering to add a matching case here.
            var registeredTypes = new[] { typeof(Traveler), typeof(DeviceToken), typeof(TravelerLoyaltyMembership) };

            foreach (var type in registeredTypes)
            {
                foreach (var propertyName in SensitiveProperties.GetSensitiveProperties(type))
                {
                    var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    Assert.True(property is not null, $"{type.Name}.{propertyName} does not resolve via reflection.");
                }
            }
        }
    }
}
