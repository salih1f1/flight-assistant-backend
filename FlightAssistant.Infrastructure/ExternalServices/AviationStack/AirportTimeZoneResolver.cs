using FlightAssistant.Application.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.AviationStack
{
    /// <summary>
    /// Turns AviationStack's airport-local timestamps into UTC.
    ///
    /// Both AviationStack flight endpoints report wall-clock time at the airport, never UTC:
    /// <c>/v1/flightsFuture</c> gives a bare <c>"2026-10-10 09:35:00"</c>, and <c>/v1/flights</c>
    /// gives <c>"2026-08-29T20:05:00+00:00"</c> — where the <c>+00:00</c> is decoration, not an
    /// offset. Reading the latter as UTC turns a 7h45m LHR→JFK flight into a 2h45m one.
    ///
    /// Zones are resolved in order: the <c>timezone</c> the response itself supplies (the
    /// real-time endpoint provides one per airport), then our own airport reference data, then
    /// AviationStack's airport lookup. That last step exists because <c>Airport.Timezone</c> is
    /// not guaranteed to be populated, and without it every schedules row would be discarded —
    /// turning a reference-data gap into an empty search result.
    ///
    /// When no source yields a usable zone the row is dropped rather than reported at a wrong
    /// time, and <see cref="UnresolvedAirports"/> records why so the caller can tell "no flights"
    /// apart from "we could not place these flights in time".
    ///
    /// Resolution is cached per instance, so one search resolves each airport once however many
    /// rows mention it.
    /// </summary>
    internal sealed class AirportTimeZoneResolver(
        IRefDataRepository refData,
        AviationStackAirportTimezoneLookup? providerLookup,
        ILogger logger)
    {
        private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Airports whose timezone could not be established, in encounter order.</summary>
        public IReadOnlyCollection<string> UnresolvedAirports => _unresolved;

        private readonly Dictionary<string, TimeZoneInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Converts an airport-local wall-clock time to UTC. Returns null when the airport's zone
        /// cannot be established.
        /// </summary>
        /// <param name="local">Wall-clock time at the airport. Any <see cref="DateTime.Kind"/> is
        /// ignored and treated as unspecified.</param>
        /// <param name="iata">Airport whose local time <paramref name="local"/> is in.</param>
        /// <param name="timezoneFromPayload">IANA id the provider supplied alongside the time, if any.</param>
        public async Task<DateTime?> ToUtcAsync(
            DateTime local,
            string iata,
            string? timezoneFromPayload,
            CancellationToken ct)
        {
            var zone = await ResolveAsync(iata, timezoneFromPayload, ct);
            if (zone is null) return null;

            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            // A time inside a spring-forward gap does not exist locally; a DST-aware conversion
            // throws rather than inventing one. Nudging forward by the gap keeps the flight
            // rather than dropping it over a once-a-year edge.
            if (zone.IsInvalidTime(unspecified))
                unspecified = unspecified.AddHours(1);

            return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
        }

        private async Task<TimeZoneInfo?> ResolveAsync(string iata, string? timezoneFromPayload, CancellationToken ct)
        {
            if (_cache.TryGetValue(iata, out var cached)) return cached;

            var id = timezoneFromPayload;

            if (string.IsNullOrWhiteSpace(id))
                id = await refData.GetAirportTimezoneByIataAsync(iata, ct);

            if (string.IsNullOrWhiteSpace(id) && providerLookup is not null)
            {
                id = await providerLookup.TryGetTimezoneAsync(iata, ct);
                if (!string.IsNullOrWhiteSpace(id))
                    logger.LogInformation(
                        "Airport {Iata} has no timezone in reference data; used the provider's ({Timezone}). "
                        + "Backfilling reference data would avoid this lookup.",
                        iata, id);
            }

            TimeZoneInfo? zone = null;

            if (string.IsNullOrWhiteSpace(id))
            {
                logger.LogWarning(
                    "No timezone available for airport {Iata} from any source; its flights are omitted",
                    iata);
            }
            else
            {
                try
                {
                    zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    logger.LogWarning(ex,
                        "Airport {Iata} has an unusable timezone {Timezone}; its flights are omitted",
                        iata, id);
                }
            }

            if (zone is null) _unresolved.Add(iata);

            _cache[iata] = zone;
            return zone;
        }
    }
}
