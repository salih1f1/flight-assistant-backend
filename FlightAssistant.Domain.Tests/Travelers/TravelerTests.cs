using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Domain.Travelers.Events;

namespace FlightAssistant.Domain.Tests.Travelers
{
    public class TravelerTests
    {
        [Fact]
        public void Register_WithValidEmail_CreatesTraveler()
        {
            var traveler = Traveler.Register("Test@Example.COM", "Alice");

            Assert.Equal("test@example.com", traveler.Email);
            Assert.Equal("Alice", traveler.DisplayName);
        }

        [Fact]
        public void Register_NormalizesEmailToLowercase()
        {
            var traveler = Traveler.Register("USER@DOMAIN.COM");
            Assert.Equal("user@domain.com", traveler.Email);
        }

        [Fact]
        public void Register_WithEmptyEmail_ThrowsDomainException()
        {
            Assert.Throws<DomainException>(() => Traveler.Register(""));
        }

        [Fact]
        public void Register_RaisesTravelerRegisteredEvent()
        {
            var traveler = Traveler.Register("alice@example.com");
            Assert.Single(traveler.DomainEvents.OfType<TravelerRegistered>());
        }

        [Fact]
        public void UpdateProfile_ChangingValues_RaisesProfileUpdatedEvent()
        {
            var traveler = Traveler.Register("alice@example.com", "Alice");
            traveler.ClearDomainEvents();

            traveler.UpdateProfile("Bob", "US");

            Assert.Equal("Bob", traveler.DisplayName);
            Assert.Equal("US", traveler.CountryCode);
            Assert.Single(traveler.DomainEvents.OfType<TravelerProfileUpdated>());
        }

        [Fact]
        public void UpdateProfile_WithSameValues_DoesNotRaiseEvent()
        {
            var traveler = Traveler.Register("alice@example.com", "Alice", "US");
            traveler.ClearDomainEvents();

            traveler.UpdateProfile("Alice", "US");

            Assert.Empty(traveler.DomainEvents.OfType<TravelerProfileUpdated>());
        }

        [Fact]
        public void AddDeviceToken_AddsToken()
        {
            var traveler = Traveler.Register("alice@example.com");
            traveler.AddDeviceToken("ios", "token123");
            Assert.Single(traveler.DeviceTokens);
        }

        [Fact]
        public void AddDeviceToken_DuplicateToken_DoesNotAddSecond()
        {
            var traveler = Traveler.Register("alice@example.com");
            traveler.AddDeviceToken("ios", "token123");
            traveler.AddDeviceToken("ios", "token123");
            Assert.Single(traveler.DeviceTokens);
        }

        [Fact]
        public void RemoveDeviceToken_RemovesCorrectToken()
        {
            var traveler = Traveler.Register("alice@example.com");
            var token = traveler.AddDeviceToken("ios", "token123");
            traveler.RemoveDeviceToken(token.Id);
            Assert.Empty(traveler.DeviceTokens);
        }

        [Fact]
        public void UpdatePassportCountry_InvalidCode_ThrowsDomainException()
        {
            var traveler = Traveler.Register("alice@example.com");
            Assert.Throws<DomainException>(() => traveler.UpdatePassportCountry("GBR")); // must be 2 chars
        }

        [Fact]
        public void UpdatePassportCountry_ValidCode_NormalizesToUppercase()
        {
            var traveler = Traveler.Register("alice@example.com");
            traveler.UpdatePassportCountry("gb");
            Assert.Equal("GB", traveler.NationalityCountryCode);
        }
    }
}
