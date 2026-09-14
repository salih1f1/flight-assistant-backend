namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// How far ahead the status provider can answer (AeroAPI's <c>end</c> bound), exposed to
    /// Application the way <see cref="IFlightSearchHorizonOptions"/> is. The planner starts Live
    /// polling this long before scheduled departure (#343).
    /// </summary>
    public interface IFlightStatusWindowOptions
    {
        int StatusWindowFutureDays { get; }
    }
}
