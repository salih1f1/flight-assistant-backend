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
        /// The ceiling is a minute because this budget is spent inside a traveler's own
        /// <c>POST /flights</c> request; the floor is a second because a zero budget would fail
        /// every provision before a source could answer.
        /// </summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary><c>00:00:00</c> disables the negative cache, matching the convention
        /// <c>Verification:IdleRelease</c> and <c>Verification:ErrorCooldown</c> already use for
        /// zero.</summary>
        [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
        public TimeSpan NegativeCacheTtl { get; set; } = TimeSpan.FromHours(24);
    }
}
