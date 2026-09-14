using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Lounges.Entities
{
    public class Lounge : IAggregateRoot, IVerifiable
    {
        public int Id { get; private set; }
        public int AirportId { get; private set; }
        public string Name { get; private set; } = default!;
        public string? Terminal { get; private set; }
        public string? LocationHint { get; private set; }
        public string? Hours { get; private set; }
        public bool IsAirside { get; private set; } = true;
        public bool IsDomesticOnly { get; private set; }
        public bool IsInternationalOnly { get; private set; }
        public string? Amenities { get; private set; }

        /// <summary>
        /// When an admin or an import added the lounge. Null when unknown: rows from before this
        /// column existed, and rows from the seed files, which nobody added (#334). The dashboard
        /// counts only a known date as new.
        /// </summary>
        public DateTime? CreatedUtc { get; private set; }

        public bool IsVerified { get; private set; }
        public DateTime? VerifiedAt { get; private set; }
        public string? VerifiedBy { get; private set; }
        public string? VerificationSource { get; private set; }
        public string? VerificationIssue { get; private set; }

        private readonly List<LoungeProviderLink> _providers = new();
        public IReadOnlyCollection<LoungeProviderLink> Providers => _providers;

        private Lounge() { } // EF

        public void Verify(string verifiedBy, string source)
        {
            IsVerified = true;
            VerifiedAt = DateTime.UtcNow;
            VerifiedBy = verifiedBy;
            VerificationSource = source;
            VerificationIssue = null;
        }

        public void Unverify()
        {
            IsVerified = false;
            VerifiedAt = null;
            VerifiedBy = null;
            VerificationSource = null;
            VerificationIssue = null;
        }

        public void MarkVerificationFailed(string issue)
        {
            IsVerified = false;
            VerifiedAt = null;
            VerifiedBy = null;
            VerificationSource = null;
            VerificationIssue = issue;
        }

        public static Lounge Create(int airportId, string name, string? terminal = null,
            string? locationHint = null, string? hours = null, bool isAirside = true,
            bool isDomesticOnly = false, bool isInternationalOnly = false, string? amenities = null) =>
            new()
            {
                AirportId = airportId,
                Name = name,
                Terminal = terminal,
                LocationHint = locationHint,
                Hours = hours,
                IsAirside = isAirside,
                IsDomesticOnly = isDomesticOnly,
                IsInternationalOnly = isInternationalOnly,
                Amenities = amenities,
                CreatedUtc = DateTime.UtcNow
            };

        /// <summary>A lounge from the seed files: as <see cref="Create"/>, but with no creation time.</summary>
        public static Lounge CreateSeeded(int airportId, string name, string? terminal = null,
            string? locationHint = null, string? hours = null, bool isAirside = true,
            bool isDomesticOnly = false, bool isInternationalOnly = false, string? amenities = null)
        {
            var lounge = Create(airportId, name, terminal, locationHint, hours, isAirside, isDomesticOnly, isInternationalOnly, amenities);
            lounge.CreatedUtc = null;
            return lounge;
        }

        public void Update(string name, string? terminal, string? locationHint, string? hours,
            bool isAirside, bool isDomesticOnly, bool isInternationalOnly, string? amenities)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new DomainException("Lounge name is required.");

            Name = name;
            Terminal = terminal;
            LocationHint = locationHint;
            Hours = hours;
            IsAirside = isAirside;
            IsDomesticOnly = isDomesticOnly;
            IsInternationalOnly = isInternationalOnly;
            Amenities = amenities;
        }

        public void ReplaceAllProviders(IEnumerable<LoungeProviderLink> newProviders)
        {
            _providers.Clear();
            foreach (var p in newProviders)
                _providers.Add(p);
        }
    }
}
