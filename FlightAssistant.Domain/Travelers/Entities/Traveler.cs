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

        public string Email { get; private set; } = default!;
        public string? DisplayName { get; private set; }

        private Traveler() { } // EF

        private Traveler(Guid id, string email, string? displayName)
        {
            if (id != Guid.Empty) Id = id;
            if (string.IsNullOrWhiteSpace(email)) throw new DomainException("Email is required.");
            Email = email.Trim().ToLowerInvariant();
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName!.Trim();

            SetCreated();
            Raise(new TravelerRegistered(Id, Email, DateTime.UtcNow));
        }

        public static Traveler Register(Guid id, string email, string? displayName = null)
            => new(id, email, displayName);

        public static Traveler Register(string email, string? displayName = null)
            => new(Guid.Empty, email, displayName);

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

        // One-way reconciliation of identity fields from the Azure AD B2C token on login.
        // Returns true if any field changed. Ignores an empty incoming email so a missing
        // claim never wipes a valid address.
        public bool SyncFromIdentity(string? email, string? displayName)
        {
            var changed = false;

            if (!string.IsNullOrWhiteSpace(email))
            {
                var normEmail = email.Trim().ToLowerInvariant();
                if (Email != normEmail) { Email = normEmail; changed = true; }
            }

            var normName = string.IsNullOrWhiteSpace(displayName) ? null : displayName!.Trim();
            if (DisplayName != normName) { DisplayName = normName; changed = true; }

            if (changed) SetUpdated();
            return changed;
        }
    }
}
