using System.ComponentModel.DataAnnotations;
using FlightAssistant.Application.ReferenceData;

namespace FlightAssistant.API.Infrastructure
{
    /// <summary>
    /// Configuration for reference-data auto-provisioning (#465), from the
    /// <c>ReferenceProvisioning</c> section. Bound and validated on start by
    /// <see cref="ReferenceProvisioningServiceCollectionExtensions.AddReferenceProvisioning"/>, so
    /// an out-of-range value stops the host from starting instead of surfacing inside a traveler's
    /// tracking request. Application reads it through <see cref="IReferenceProvisioningOptions"/>.
    /// </summary>
    public sealed class ReferenceProvisioningOptions : IReferenceProvisioningOptions
    {
        public const string SectionName = "ReferenceProvisioning";

        /// <inheritdoc />
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// One wall-clock budget for the whole provisioning step.
        ///
        /// <para>
        /// <b>It must comfortably exceed <c>AeroApi:MinRequestInterval</c> (12 s by default).</b>
        /// The airline source is <c>aeroapi</c>, which goes over the shared <c>"AeroApi"</c> named
        /// client behind <c>AeroApiThrottlingHandler</c>: a caller can wait a full
        /// <c>MinRequestInterval</c> for its throttle slot <em>before the request is even sent</em>,
        /// and that client's own sanctioned per-lookup budget (<c>AeroApiOptions.AttemptTimeout</c>)
        /// is 42 s. Worse, a metered source is gated on <c>IProviderBudgetGuard.GetStateAsync</c>
        /// first, so a cold <c>AeroApiUsageCache</c> makes that read a second throttled call --
        /// roughly 24 s of pure waiting. At the old 10 s default, airline provisioning therefore
        /// succeeded only when the gate happened to be idle, and an aborted in-flight request could
        /// still be billed with its result discarded. This is the same defect class CLAUDE.md
        /// records with the identical two numbers ("the library's 10 s default against a 12 s
        /// interval"), whose rule is that every timeout over this client is derived from
        /// <c>MinRequestInterval</c> and never hardcoded -- which a documented constant cannot be,
        /// so the relationship is stated here instead of computed. Tune <c>MinRequestInterval</c>
        /// upward and this has to move with it.
        /// </para>
        ///
        /// <para>
        /// The ceiling is two minutes because this budget is spent inside a traveler's own
        /// <c>POST /flights</c> request; the floor is a second because a zero budget would fail
        /// every provision before a source could answer.
        /// </para>
        /// </summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary><c>00:00:00</c> disables the negative cache, matching the convention
        /// <c>Verification:IdleRelease</c> and <c>Verification:ErrorCooldown</c> already use for
        /// zero.</summary>
        [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
        public TimeSpan NegativeCacheTtl { get; set; } = TimeSpan.FromHours(24);
    }
}
