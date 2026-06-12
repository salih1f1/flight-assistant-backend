namespace FlightAssistant.Domain.Common.Interfaces
{
    public interface ISeedableEntity
    {
        string SourceType { get; set; }
        string? SourceSystem { get; set; }
        string? ExternalIds { get; set; }
        string Status { get; set; }
        DateTime FirstSeenAt { get; }
        DateTime LastSeenAt { get; }
        string? Notes { get; set; }
    }
}
