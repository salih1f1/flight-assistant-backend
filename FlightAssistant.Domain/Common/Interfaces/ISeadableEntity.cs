namespace FlightAssistant.Domain.Common.Interfaces
{
    public interface ISeadableEntity
    {
        string SourceType { get; set; }
        string? SourceSystem { get; set; }
        string? ExternalIds { get; set; }
        string Status { get; set; }
        DateTime FirstSeenAt { get; set; }
        DateTime LastSeenAt { get; set; }
        string? Notes { get; set; }
    }
}
