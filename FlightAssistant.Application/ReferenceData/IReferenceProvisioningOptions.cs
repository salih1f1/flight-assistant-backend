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
        /// <summary>
        /// Whether a missing airline, airport or country is looked up in the external sources at
        /// all. When false, <c>POST /flights</c> answers the pre-#465 404 for a missing reference --
        /// but not quite the pre-#465 behaviour for every request: the tracking cap is checked
        /// <em>before</em> provisioning (this flag is read inside
        /// <see cref="IReferenceDataProvisioner.ProvisionAsync"/>, one step later), so an at-cap
        /// traveler naming an unknown reference still gets 409 where pre-#465 they got 404.
        /// </summary>
        bool Enabled { get; }

        /// <summary>
        /// One wall-clock budget for the whole provisioning step, however many entities and sources
        /// it touches. Exceeding it is a 503, never a 404: we did not learn that the entity is
        /// absent, only that we ran out of time. For <c>ourairports</c> and <c>mledoze</c> the
        /// shared dataset download keeps running afterwards, so a retry finds it warm -- but
        /// <c>aeroapi</c> caches nothing (#401 R6: <c>HasCachedData</c> is always false), so an
        /// aborted airline lookup leaves nothing warm and the retry starts over behind
        /// <c>AeroApiThrottleGate</c>. The airline path can therefore keep answering 503 while that
        /// gate is busy, which is why this value has to exceed <c>AeroApi:MinRequestInterval</c>
        /// with room to spare.
        /// </summary>
        TimeSpan Budget { get; }

        /// <summary>How long "this source has no such entity" is remembered, so a repeated bogus
        /// flight designator costs nothing after the first attempt. <see cref="TimeSpan.Zero"/>
        /// disables the cache, so every attempt re-queries the source.</summary>
        TimeSpan NegativeCacheTtl { get; }
    }
}
