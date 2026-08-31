using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// A <see cref="DelegatingHandler"/> registered on every AeroAPI <see cref="HttpClient"/> that
    /// waits its turn on the shared <see cref="AeroApiThrottleGate"/> before letting a request
    /// through. Registered inside (closer to the transport than) the standard resilience handler in
    /// DI, so every retry attempt is throttled too, not just the first attempt of a call.
    /// </summary>
    public sealed class AeroApiThrottlingHandler : DelegatingHandler
    {
        private readonly AeroApiThrottleGate _gate;
        private readonly IOptionsMonitor<AeroApiOptions> _options;

        public AeroApiThrottlingHandler(AeroApiThrottleGate gate, IOptionsMonitor<AeroApiOptions> options)
        {
            _gate = gate;
            _options = options;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _gate.WaitForTurnAsync(_options.CurrentValue.MinRequestInterval, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
