namespace FlightAssistant.Application.ReferenceData
{
    /// <summary>
    /// The <c>ReferenceProvisioning</c> configuration section as Application sees it (#465) --
    /// the same translation-interface pattern as <c>IVerificationJobOptions</c> and
    /// <c>IFlightSyncOptions</c>, so Application never reads <c>IConfiguration</c>. Bound and
    /// validated on start in the API layer.
    /// </summary>
    public interface IReferenceProvisioningOptions
    {
        /// <summary>Whether a missing airline, airport or country is looked up in the external
        /// sources at all. When false, <c>POST /flights</c> answers exactly the 404 it did before
        /// #465.</summary>
        bool Enabled { get; }

        /// <summary>One wall-clock budget for the whole provisioning step, however many entities
        /// and sources it touches. Exceeding it is a 503, never a 404: we did not learn that the
        /// entity is absent, only that we ran out of time. The shared dataset download keeps
        /// running afterwards, so a retry finds it warm.</summary>
        TimeSpan Budget { get; }

        /// <summary>How long "this source has no such entity" is remembered, so a repeated bogus
        /// flight designator costs nothing after the first attempt. <see cref="TimeSpan.Zero"/>
        /// disables the cache, so every attempt re-queries the source.</summary>
        TimeSpan NegativeCacheTtl { get; }
    }
}
