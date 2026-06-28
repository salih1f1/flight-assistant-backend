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
        public void SyncFromIdentity_ChangedValues_UpdatesFieldsAndReturnsTrue()
        {
            var traveler = Traveler.Register("alice@example.com", "Alice");

            var changed = traveler.SyncFromIdentity("alice.new@example.com", "Alice Smith");

            Assert.True(changed);
            Assert.Equal("alice.new@example.com", traveler.Email);
            Assert.Equal("Alice Smith", traveler.DisplayName);
        }

        [Fact]
        public void SyncFromIdentity_SameValues_ReturnsFalse()
        {
            var traveler = Traveler.Register("alice@example.com", "Alice");

            var changed = traveler.SyncFromIdentity("alice@example.com", "Alice");

            Assert.False(changed);
        }

        [Fact]
        public void SyncFromIdentity_NormalizesEmail()
        {
            var traveler = Traveler.Register("alice@example.com", "Alice");

            traveler.SyncFromIdentity("ALICE.NEW@Example.COM", "Bob");

            Assert.Equal("alice.new@example.com", traveler.Email);
        }

        [Fact]
        public void SyncFromIdentity_EmptyEmail_DoesNotOverwriteExistingEmail()
        {
            var traveler = Traveler.Register("alice@example.com", "Alice");

            traveler.SyncFromIdentity("  ", "Alice");

            Assert.Equal("alice@example.com", traveler.Email);
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
    }
}
