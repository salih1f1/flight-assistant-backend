using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// Which sources check one entity type, honouring the runtime switch (#531) and falling back to
    /// <c>Verification:DefaultSources:*</c>.
    ///
    /// <para>
    /// <b>This does not reach the verification job.</b> The job uses each request's own stored
    /// <c>sources</c>, so changing a switch affects newly <i>queued</i> requests and the track-time
    /// provisioning path, and leaves everything already in the queue alone.
    /// </para>
    /// </summary>
    public sealed class VerificationSourceDefaults : IVerificationSourceDefaults
    {
        private readonly IProviderRouting _routing;
        private readonly IVerificationJobOptions _options;

        public VerificationSourceDefaults(IProviderRouting routing, IVerificationJobOptions options)
        {
            _routing = routing;
            _options = options;
        }

        public async Task<IReadOnlyList<string>> ForAsync(VerificationEntityType entityType, CancellationToken ct)
        {
            var chosen = await _routing.GetProvidersAsync(ProviderJobs.ForEntityType(entityType), ct);
            return chosen.Count > 0 ? chosen : _options.DefaultSourcesFor(entityType);
        }
    }
}
