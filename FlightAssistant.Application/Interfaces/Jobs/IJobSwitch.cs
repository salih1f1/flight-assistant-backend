using FlightAssistant.Application.Jobs;

namespace FlightAssistant.Application.Interfaces.Jobs
{
    /// <summary>
    /// The runtime on/off switch for a background job, read on every loop tick.
    ///
    /// <para>
    /// Answers the switch only, never the config ceiling: <c>FlightSync:Enabled</c> is read where
    /// the hosted service starts, and a caller combines the two. Keeping them apart is what stops
    /// a <c>true</c> here from being read as "this job is running".
    /// </para>
    /// </summary>
    public interface IJobSwitch
    {
        /// <summary>Cached, so a one-minute loop does not hit the database every tick for a value
        /// that changes twice a month. The cache window bounds how long a toggle takes to apply.</summary>
        Task<bool> IsEnabledAsync(string job, CancellationToken ct);

        /// <summary>The full row, for the admin endpoint. Uncached — an operator reading the page
        /// after pressing the button must see what they just did, not a stale cache entry.</summary>
        Task<JobSwitchState> GetAsync(string job, CancellationToken ct);
    }
}
