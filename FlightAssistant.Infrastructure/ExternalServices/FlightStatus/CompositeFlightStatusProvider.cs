using FlightAssistant.Application.Interfaces.ExternalServices;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightStatus
{
    /// <summary>Tries AeroDataBox first; falls back to AviationStack on failure.</summary>
    public sealed class CompositeFlightStatusProvider : IFlightStatusProvider
    {
        private readonly AeroDataBoxFlightStatusProvider _primary;
        private readonly AviationStackFlightStatusProvider _fallback;
        private readonly ILogger<CompositeFlightStatusProvider> _logger;

        public string ProviderName => _primary.ProviderName;

        public CompositeFlightStatusProvider(
            AeroDataBoxFlightStatusProvider primary,
            AviationStackFlightStatusProvider fallback,
            ILogger<CompositeFlightStatusProvider> logger)
        {
            _primary = primary;
            _fallback = fallback;
            _logger = logger;
        }

        public async Task<FlightStatusResult?> GetStatusAsync(string flightNumber, DateOnly serviceDate, CancellationToken ct = default)
        {
            try
            {
                var result = await _primary.GetStatusAsync(flightNumber, serviceDate, ct);
                if (result is not null) return result;

                _logger.LogWarning("AeroDataBox returned no result for {FlightNumber} {Date}; trying fallback.", flightNumber, serviceDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AeroDataBox failed for {FlightNumber} {Date}; trying fallback.", flightNumber, serviceDate);
            }

            try
            {
                return await _fallback.GetStatusAsync(flightNumber, serviceDate, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Both flight status providers failed for {FlightNumber} {Date}.", flightNumber, serviceDate);
                return null;
            }
        }
    }
}
