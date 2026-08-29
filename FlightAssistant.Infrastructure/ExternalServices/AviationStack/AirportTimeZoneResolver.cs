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
    /// Zones come from the provider, never from our own reference data: first the <c>timezone</c>
    /// the response itself supplies (the real-time endpoint gives one per airport), then
    /// AviationStack's airport lookup for the schedules endpoint, which supplies none.
    ///
    /// <c>Airport.Timezone</c> is deliberately not consulted. It is imported by hand, so it can be
    /// absent, stale, or hold the wrong thing outright — and a present-but-invalid value is worse
    /// than a missing one, because it looks authoritative. The timestamps being converted come
    /// from AviationStack, so the zone that interprets them comes from AviationStack too.
    ///
    /// When no source yields a usable zone the row is dropped rather than reported at a wrong
    /// time, and <see cref="UnresolvedAirports"/> records why so the caller can tell "no flights"
    /// apart from "we could not place these flights in time".
    ///
    /// Resolution is cached per instance, so one search resolves each airport once however many
    /// rows mention it.
    /// </summary>
    internal sealed class AirportTimeZoneResolver(
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

        /// <summary>
        /// Takes the zone from the response when it carries one, otherwise asks the provider's
        /// airport lookup. Each candidate must survive
        /// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> before it is accepted, so a malformed
        /// value falls through rather than blocking the next source.
        /// </summary>
        private async Task<TimeZoneInfo?> ResolveAsync(string iata, string? timezoneFromPayload, CancellationToken ct)
        {
            if (_cache.TryGetValue(iata, out var cached)) return cached;

            var zone = TryZone(timezoneFromPayload, iata, "the provider response");

            if (zone is null && providerLookup is not null)
                zone = TryZone(await providerLookup.TryGetTimezoneAsync(iata, ct), iata, "the provider's airport lookup");

            if (zone is null)
            {
                logger.LogWarning(
                    "No usable IANA timezone for airport {Iata} from any source; its flights are omitted",
                    iata);
                _unresolved.Add(iata);
            }

            _cache[iata] = zone;
            return zone;
        }

        /// <summary>
        /// Turns a candidate id into a zone, or null if it is absent or not a usable IANA id.
        /// A rejected candidate is logged with its source, because the common cause — the wrong
        /// column imported into reference data — is otherwise invisible.
        /// </summary>
        private TimeZoneInfo? TryZone(string? id, string iata, string source)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                logger.LogWarning(
                    "Airport {Iata}: {Source} gave {Timezone}, which is not a usable IANA timezone id. "
                    + "Falling through to the next source. ({Reason})",
                    iata, source, id, ex.Message);
                return null;
            }
        }
    }
}
