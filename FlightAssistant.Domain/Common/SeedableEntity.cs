using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Common
{
    public class SeedableEntity : ISeedableEntity
    {
        public string SourceType { get; set; } = "seed";
        public string? SourceSystem { get; set; }
        public string? ExternalIds { get; set; } // JSON string of key-value pairs
        public string Status { get; set; } = "active";
        public DateTime FirstSeenAt { get; set; } =  DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        public string? Notes { get; set; }
    }
}
