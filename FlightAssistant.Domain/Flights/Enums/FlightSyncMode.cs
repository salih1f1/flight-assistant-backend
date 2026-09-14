namespace FlightAssistant.Domain.Flights.Enums
{
    /// <summary>How often the status sync checks a tracked flight. See FlightSyncPlanner.</summary>
    public enum FlightSyncMode
    {
        Fast,
        Live,
        Schedule,
        Stopped
    }
}
