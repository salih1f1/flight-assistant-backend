namespace FlightAssistant.Application.FlightSync
{
    /// <summary>The <c>FlightSync</c> settings the planner needs (spec §2.7). Implemented by the
    /// API's <c>FlightSyncOptions</c>, which is bound and validated on start.</summary>
    public interface IFlightSyncOptions
    {
        /// <summary>Fast interval.</summary>
        TimeSpan NearDepartureInterval { get; }

        /// <summary>Live interval.</summary>
        TimeSpan Interval { get; }

        TimeSpan ScheduleCheckInterval { get; }
        TimeSpan FastWindowBeforeDeparture { get; }
        TimeSpan StopAfterArrival { get; }
        TimeSpan StopWithoutArrivalAfter { get; }
    }
}
