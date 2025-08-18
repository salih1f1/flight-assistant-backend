using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Travelers.Events;

namespace FlightAssistant.Domain.Travelers.Entities
{
    public sealed class Traveler : AuditableEntity, IAggregateRoot
    {
        private readonly List<DeviceToken> _deviceTokens = new();
        public IReadOnlyCollection<DeviceToken> DeviceTokens => _deviceTokens.AsReadOnly();

        public string Email { get; private set; }
        public string? DisplayName { get; private set; }
        public string? CountryCode { get; private set; }
        public string? NationalityCountryCode { get; private set; }
        public string? ResidenceCountryCode { get; private set; }

        private Traveler() { } // EF

        private Traveler(string email, string? displayName, string? countryCode)
        {
            if (string.IsNullOrWhiteSpace(email)) throw new DomainException("Email is required.");
            Email = email.Trim().ToLowerInvariant();
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName!.Trim();
            CountryCode = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode!.ToUpperInvariant();

            SetCreated();
            Raise(new TravelerRegistered(Id, Email, DateTime.UtcNow));
        }

        public static Traveler Register(string email, string? displayName = null, string? countryCode = null)
            => new(email, displayName, countryCode);

        public void UpdateProfile(string? displayName, string? countryCode)
        {
            var changed = false;

            var normName = string.IsNullOrWhiteSpace(displayName) ? null : displayName!.Trim();
            var normCountry = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode!.ToUpperInvariant();

            if (DisplayName != normName) { DisplayName = normName; changed = true; }
            if (CountryCode != normCountry) { CountryCode = normCountry; changed = true; }

            if (changed)
            {
                SetUpdated();
                Raise(new TravelerProfileUpdated(Id, DisplayName, CountryCode, DateTime.UtcNow));
            }
        }

        public DeviceToken AddDeviceToken(string platform, string token, DateTime? seenUtc = null)
        {
            if (string.IsNullOrWhiteSpace(platform)) throw new DomainException("Platform is required.");
            if (string.IsNullOrWhiteSpace(token)) throw new DomainException("Token is required.");

            // dedupe: same token for this traveler
            var existing = _deviceTokens.FirstOrDefault(t => t.Token == token);
            if (existing is not null)
            {
                existing.Touch(seenUtc ?? DateTime.UtcNow);
                SetUpdated();
                return existing;
            }

            var dt = new DeviceToken(Id, platform.Trim(), token.Trim(), seenUtc ?? DateTime.UtcNow);
            _deviceTokens.Add(dt);
            SetUpdated();
            Raise(new DeviceTokenAdded(Id, dt.Id, dt.Platform, dt.Token, DateTime.UtcNow));
            return dt;
        }

        public void RemoveDeviceToken(Guid deviceTokenId)
        {
            var removed = _deviceTokens.RemoveAll(x => x.Id == deviceTokenId) > 0;
            if (removed)
            {
                SetUpdated();
                Raise(new DeviceTokenRemoved(Id, deviceTokenId, DateTime.UtcNow));
            }
        }

        public void UpdatePassportCountry(string countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
                throw new DomainException("Passport country must be ISO alpha-2.");
            NationalityCountryCode = countryCode.ToUpperInvariant();
            SetUpdated();
        }

        public void UpdateResidenceCountry(string? countryCode)
        {
            ResidenceCountryCode = string.IsNullOrWhiteSpace(countryCode)
                ? null
                : countryCode!.ToUpperInvariant();
            SetUpdated();
        }
    }
}
