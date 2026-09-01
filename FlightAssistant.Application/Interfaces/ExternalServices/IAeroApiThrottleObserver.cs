namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// Read-only view of the AeroAPI outbound throttle's next reserved send slot, so
    /// <c>GetAeroApiUsageQueryHandler</c> can surface "next AeroAPI slot in Ns" on the usage
    /// dashboard — explaining a slow search at a glance — without Application referencing
    /// Infrastructure directly. <c>AeroApiThrottleGate</c> (Infrastructure) implements this
    /// interface itself and is registered under it as the very same singleton instance — the same
    /// translation pattern used for <see cref="IFlightSearchHorizonOptions"/> and
    /// <see cref="IAeroApiBudgetOptions"/>. See the DI registration in
    /// <c>FlightAssistant.Infrastructure.DependencyInjection</c>.
    /// </summary>
    public interface IAeroApiThrottleObserver
    {
        /// <summary>
        /// The instant the next outbound AeroAPI call is due, or null if no call has been reserved
        /// yet. Purely an observation of existing throttle state — reading it never reserves,
        /// releases, or otherwise mutates a slot.
        /// </summary>
        DateTimeOffset? NextSlotUtc { get; }
    }
}
