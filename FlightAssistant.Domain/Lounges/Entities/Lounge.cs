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

        public bool IsVerified { get; private set; }
        public DateTime? VerifiedAt { get; private set; }
        public string? VerifiedBy { get; private set; }

        public ICollection<LoungeProviderLink> Providers { get; private set; } = new List<LoungeProviderLink>();

        public void Verify(string verifiedBy)
        {
            IsVerified = true;
            VerifiedAt = DateTime.UtcNow;
            VerifiedBy = verifiedBy;
        }

        public void Unverify()
        {
            IsVerified = false;
            VerifiedAt = null;
            VerifiedBy = null;
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
                Amenities = amenities
            };

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
    }
}
