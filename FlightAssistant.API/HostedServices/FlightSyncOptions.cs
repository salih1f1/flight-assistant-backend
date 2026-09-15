using System.ComponentModel.DataAnnotations;
using FlightAssistant.Application.FlightSync;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// The <c>FlightSync</c> section (spec §2.7). Bound and validated on start by
    /// <see cref="FlightSyncServiceCollectionExtensions.AddFlightSync"/>. Application reads it
    /// through <see cref="IFlightSyncOptions"/>.
    /// </summary>
    public sealed class FlightSyncOptions : IFlightSyncOptions
    {
        public const string SectionName = "FlightSync";

        public bool Enabled { get; set; } = true;

        /// <summary>How often the background service looks for due flights.</summary>
        [Range(typeof(TimeSpan), "00:00:10", "00:10:00")]
        public TimeSpan LoopInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>The Fast interval.</summary>
        [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
        public TimeSpan NearDepartureInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>The Live interval.</summary>
        [Range(typeof(TimeSpan), "00:01:00", "06:00:00")]
        public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);

        [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
        public TimeSpan ScheduleCheckInterval { get; set; } = TimeSpan.FromDays(1);

        [Range(typeof(TimeSpan), "00:30:00", "1.00:00:00")]
        public TimeSpan FastWindowBeforeDeparture { get; set; } = TimeSpan.FromHours(4);

        [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
        public TimeSpan StopAfterArrival { get; set; } = TimeSpan.FromMinutes(30);

        [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
        public TimeSpan StopWithoutArrivalAfter { get; set; } = TimeSpan.FromDays(1);

        /// <summary>A traveler's manual sync within this long of the flight's last sync makes no
        /// provider call (#369). Zero turns it off.</summary>
        [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
        public TimeSpan ManualSyncCooldown { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>How many tracked flights the status sync still polls a traveler can have at once
        /// (#380). A flight holds its slot until the planner stops it, so this bounds how many flights
        /// one account keeps polled.</summary>
        [Range(1, 1000)]
        public int MaxActiveTrackedFlightsPerTraveler { get; set; } = 20;
    }
}
