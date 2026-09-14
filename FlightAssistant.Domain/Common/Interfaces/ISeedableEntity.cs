namespace FlightAssistant.Domain.Common.Interfaces
{
    public interface ISeedableEntity
    {
        string SourceType { get; set; }
        DateTime FirstSeenAt { get; }
    }
}
