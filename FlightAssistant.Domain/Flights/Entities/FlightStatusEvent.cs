using System.Security.Cryptography;
using System.Text;

namespace FlightAssistant.Domain.Flights.Entities
{
    public sealed class FlightStatusEvent
    {
        public long Id { get; private set; }
        public int FlightId { get; private set; }
        public int StatusId { get; private set; }

        public DateTime EffectiveAtUtc { get; private set; }
        public DateTime RecordedAtUtc { get; private set; }
        public string Source { get; private set; } = "manual";
        public string? ExternalId { get; private set; }
        public string? Gate { get; private set; }
        public string? Terminal { get; private set; }
        public string? BaggageClaim { get; private set; }
        public string? Remarks { get; private set; }
        public string? RawPayloadJson { get; private set; }
        public string DedupHash { get; private set; } = default!;

        public Flight Flight { get; private set; } = default!;
        public FlightStatus Status { get; private set; } = default!;

        private FlightStatusEvent() { } // EF

        public static FlightStatusEvent Create(
            int flightId,
            int statusId,
            DateTime effectiveAtUtc,
            string source,
            string? externalId = null,
            string? gate = null,
            string? terminal = null,
            string? baggageClaim = null,
            string? remarks = null,
            string? rawPayloadJson = null)
        {
            var evt = new FlightStatusEvent
            {
                FlightId = flightId,
                StatusId = statusId,
                EffectiveAtUtc = effectiveAtUtc,
                RecordedAtUtc = DateTime.UtcNow,
                Source = source,
                ExternalId = externalId,
                Gate = gate,
                Terminal = terminal,
                BaggageClaim = baggageClaim,
                Remarks = remarks,
                RawPayloadJson = rawPayloadJson
            };
            evt.DedupHash = ComputeDedupHash(flightId, statusId, effectiveAtUtc, source, externalId);
            return evt;
        }

        /// <summary>
        /// Computes the dedup hash for a logical status event. Used both by <see cref="Create"/> to set the
        /// stored <see cref="DedupHash"/> and by handlers for the pre-insert dedup existence check — these MUST
        /// use identical inputs so the pre-check matches the stored hash for the same logical event.
        /// </summary>
        public static string ComputeDedupHash(int flightId, int statusId, DateTime effectiveAt, string source, string? externalId)
        {
            var raw = $"{flightId}|{statusId}|{effectiveAt:O}|{source}|{externalId}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes)[..16];
        }
    }
}
