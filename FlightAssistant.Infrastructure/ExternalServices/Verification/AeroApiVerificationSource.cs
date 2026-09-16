using System.Net;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>
    /// Verification source for airports and airlines against FlightAware AeroAPI itself (#401,
    /// issue #367 step 9) -- the metered counterpart to the two free dataset sources beside it.
    ///
    /// <para>
    /// <b>Endpoints and matching (R2).</b> <c>GET /airports/{id}</c> for
    /// <see cref="VerificationEntityType.Airport"/>, <c>GET /operators/{id}</c> for
    /// <see cref="VerificationEntityType.Airline"/> -- AeroAPI calls an airline an "operator". Both
    /// prefer <see cref="VerificationSourceLookupKey.Icao"/>, AeroAPI's own identifier, falling back
    /// to <see cref="VerificationSourceLookupKey.Iata"/> only when it is empty -- never as a second
    /// call after the preferred one fails. When both are empty, the lookup is
    /// <see cref="VerificationSourceFetchResult.NotFound"/> without sending a request at all.
    /// </para>
    ///
    /// <para>
    /// <b>Errors (R3).</b> A 404 means AeroAPI has no row for this airport/operator -- a real
    /// answer, so it maps to <see cref="VerificationSourceFetchResult.NotFound"/>, not an error.
    /// Every other failure (5xx, 429, a network failure, a timeout, a 200 whose body will not parse)
    /// is a retryable <see cref="VerificationSourceFetchResult.SourceError"/> whose text is one of
    /// the fixed categories below plus a fact this code controls (an HTTP status code) -- never an
    /// upstream exception's message, matching <see cref="OurAirportsVerificationSource"/> and
    /// <see cref="MledozeCountriesVerificationSource"/>'s own convention for
    /// <c>verification_requests.error</c>, which admins see in every environment. This source does
    /// call <see cref="AeroApiResponse.ReadOrThrowAsync"/> -- every other AeroAPI caller does, for
    /// its credential redaction and its own status-code logging, and there is no reason this one
    /// caller alone should re-implement that -- but it never surfaces
    /// <see cref="DomainException.Message"/> into the stored error: that text is phrased for a
    /// traveler-facing flight-data response ("Flight data is temporarily unavailable."), not for
    /// this column. The raw HTTP status is captured before the call, exactly as
    /// <see cref="AeroApiAirportTimezoneLookup.FetchAsync"/> already does, so the 404-vs-everything-
    /// else decision (and the fixed-category text) does not depend on parsing the exception at all.
    /// </para>
    ///
    /// <para>
    /// <b>Timezone cache (R7).</b> A successful airport lookup already paid for the call, so it
    /// opportunistically fills <see cref="AeroApiAirportTimezoneLookup"/>'s own cache with the
    /// response's <c>timezone</c> -- through <see cref="AeroApiAirportTimezoneLookup.CacheKeyFor"/>,
    /// the exact key that lookup itself reads, under the same <see cref="AeroApiAirportTimezoneLookup.SuccessCacheDuration"/>.
    /// The key is built from the response's own <c>code_iata</c>, not this call's requested
    /// <see cref="VerificationSourceLookupKey.Iata"/>: the latter is <i>our</i> stored value, which
    /// this very check exists to catch when it has drifted, and <see cref="AeroApiAirportTimezoneLookup"/>
    /// itself is always looked up by IATA (a flight search's origin), so keying by anything else --
    /// our possibly-stale code, or the ICAO code this lookup may have preferred -- would write an
    /// entry nothing ever reads back. This source never <i>reads</i> that cache to answer a check
    /// (R6 -- it holds nothing of its own either way).
    /// </para>
    ///
    /// <para>
    /// <b>No dataset (R6).</b> Unlike the two free sources, there is nothing to download and reuse:
    /// every lookup is its own billed call. <see cref="HasCachedData"/> is always false and
    /// <see cref="ReleaseCache"/> does nothing.
    /// </para>
    ///
    /// <para>
    /// <b>Singleton, like the other two sources.</b> <c>IVerificationSourceRegistry</c> is itself a
    /// singleton built from <c>IEnumerable&lt;IVerificationSource&gt;</c>, so every registered source
    /// must be safe to resolve once and hold forever. This source is stateless -- no dataset to
    /// persist across a job's lifetime the way the other two need to -- but it still takes
    /// <see cref="IHttpClientFactory"/> rather than a pre-created <see cref="HttpClient"/>, calling
    /// <see cref="IHttpClientFactory.CreateClient(string)"/> fresh for every lookup: a client
    /// captured once at registration time would outlive its <c>HttpMessageHandler</c> for the whole
    /// process, the exact anti-pattern <c>DependencyInjection.AddInfrastructure</c> documents for
    /// the free sources. It calls the shared <c>"AeroApi"</c> named client -- the same one flight
    /// search, flight status and the usage report use -- so a verification lookup is authenticated,
    /// retried and throttled by <see cref="AeroApiThrottleGate"/> exactly like every other AeroAPI
    /// call, not a second, ungoverned path to the same metered API.
    /// </para>
    /// </summary>
    public sealed class AeroApiVerificationSource : IVerificationSource
    {
        /// <summary>The name this source is registered and resolved under, and the value recorded
        /// in <c>verification_requests.sources</c> / <c>IVerifiable.VerificationSource</c>.</summary>
        public const string SourceName = "aeroapi";

        /// <summary>The shared named <see cref="HttpClient"/> this source calls through --
        /// <c>DependencyInjection.AddInfrastructure</c>'s <c>"AeroApi"</c> client (auth, resilience,
        /// throttle), not a dedicated one of its own the way the free dataset sources have.</summary>
        public const string HttpClientName = "AeroApi";

        private const string AirportOperation = "Airport lookup";
        private const string AirlineOperation = "Operator lookup";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ICacheService _cache;
        private readonly string _apiKey;
        private readonly ILogger<AeroApiVerificationSource> _logger;

        public AeroApiVerificationSource(
            IHttpClientFactory httpClientFactory,
            ICacheService cache,
            IOptions<AeroApiOptions> options,
            ILogger<AeroApiVerificationSource> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _apiKey = options.Value.ApiKey;
            _logger = logger;
        }

        public string Name => SourceName;

        /// <summary>AeroAPI's personal-tier per-row cost (#401 task brief), summed into
        /// <c>VerificationRequest.EstimatedCostUsd</c> by the queue.</summary>
        public decimal CostPerRowUsd => 0.015m;

        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes { get; } =
            new[] { VerificationEntityType.Airport, VerificationEntityType.Airline };

        public async Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            if (!SupportedEntityTypes.Contains(entityType))
                throw new ArgumentOutOfRangeException(nameof(entityType), entityType, $"{SourceName} does not support {entityType}.");

            // R2: ICAO preferred, IATA only when ICAO is empty -- never a second call if the
            // preferred one fails. Both empty is NotFound without sending anything.
            var code = string.IsNullOrWhiteSpace(key.Icao) ? key.Iata : key.Icao;
            if (string.IsNullOrWhiteSpace(code))
                return VerificationSourceFetchResult.NotFound;

            var segment = entityType == VerificationEntityType.Airport ? "airports" : "operators";
            var operation = entityType == VerificationEntityType.Airport ? AirportOperation : AirlineOperation;
            var http = _httpClientFactory.CreateClient(HttpClientName);

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync($"{segment}/{Uri.EscapeDataString(code)}", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The resilience pipeline's own attempt/total timeout fired, not this call's ct.
                _logger.LogWarning("Timed out calling {SourceName} for {Operation} {Code}", SourceName, operation, code);
                return VerificationSourceFetchResult.SourceError(TimedOutError);
            }
            catch (TimeoutRejectedException)
            {
                // Final fix G3: the same timeout, a different shape. The standard resilience
                // handler's own timeout strategy throws Polly.Timeout.TimeoutRejectedException
                // directly -- it does not derive from OperationCanceledException, so without this
                // catch it fell into the generic one below and was misreported as a network error.
                _logger.LogWarning("Timed out calling {SourceName} for {Operation} {Code}", SourceName, operation, code);
                return VerificationSourceFetchResult.SourceError(TimedOutError);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not reach {SourceName} for {Operation} {Code}", SourceName, operation, code);
                return VerificationSourceFetchResult.SourceError(NetworkError);
            }

            // Captured before ReadOrThrowAsync, which maps a 404 to the same generic 502 as every
            // other unclassified failure -- R3 needs the real status to tell them apart.
            var status = response.StatusCode;

            try
            {
                using var doc = await AeroApiResponse.ReadOrThrowAsync(response, operation, code, _apiKey, _logger, ct);
                var root = doc.RootElement;

                if (entityType == VerificationEntityType.Airport)
                    await FillTimezoneCacheAsync(root, ct);

                var values = entityType == VerificationEntityType.Airport ? MapAirport(root) : MapAirline(root);
                return VerificationSourceFetchResult.Found(values);
            }
            catch (DomainException ex)
            {
                if (status == HttpStatusCode.NotFound)
                    return VerificationSourceFetchResult.NotFound;

                _logger.LogWarning(ex, "{SourceName} {Operation} for {Code} failed: {Message}", SourceName, operation, code, ex.Message);

                // A success status here means ReadOrThrowAsync's own JSON parse failed -- the body
                // arrived but was not valid JSON -- distinct from a non-success HTTP status.
                var error = response.IsSuccessStatusCode ? ParseError : HttpStatusError(status);
                return VerificationSourceFetchResult.SourceError(error);
            }
        }

        /// <summary>R6: nothing is ever cached, so there is nothing to release.</summary>
        public void ReleaseCache(TimeSpan idleRelease)
        {
            // Intentionally empty.
        }

        /// <summary>R6: every lookup is its own call; this source never holds a dataset.</summary>
        public bool HasCachedData => false;

        /// <summary>
        /// R7: fills <see cref="AeroApiAirportTimezoneLookup"/>'s cache from this already-paid-for
        /// airport response. Keyed by the response's own <c>code_iata</c> (see the class doc's
        /// "Timezone cache" remark for why, not this call's requested key), and skipped entirely
        /// when either is missing -- an airport AeroAPI reports with no IATA code, or no timezone,
        /// leaves nothing this cache could ever be read back by.
        /// </summary>
        private async Task FillTimezoneCacheAsync(JsonElement root, CancellationToken ct)
        {
            var iata = GetString(root, "code_iata");
            var timezone = GetString(root, "timezone");
            if (string.IsNullOrWhiteSpace(iata) || string.IsNullOrWhiteSpace(timezone))
                return;

            await _cache.SetAsync(
                AeroApiAirportTimezoneLookup.CacheKeyFor(iata),
                timezone,
                AeroApiAirportTimezoneLookup.SuccessCacheDuration,
                ct);
        }

        private static IReadOnlyDictionary<string, string?> MapAirport(JsonElement root)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in VerificationCheckedColumns.GetColumns(VerificationEntityType.Airport))
            {
                values[column.Name] = column.Name switch
                {
                    nameof(Airport.Iata) => GetString(root, "code_iata"),
                    nameof(Airport.Icao) => GetString(root, "code_icao"),
                    nameof(Airport.Name) => GetString(root, "name"),
                    nameof(Airport.City) => GetString(root, "city"),
                    nameof(Airport.CountryCode) => GetString(root, "country_code"),
                    nameof(Airport.Latitude) => GetRawNumber(root, "latitude"),
                    nameof(Airport.Longitude) => GetRawNumber(root, "longitude"),
                    // AeroAPI's `elevation` is already feet -- no unit conversion here.
                    nameof(Airport.ElevationFt) => GetRawNumber(root, "elevation"),
                    _ => throw new InvalidOperationException(
                        $"{SourceName} has no mapping for checked Airport column '{column.Name}'.")
                };
            }

            return values;
        }

        private static IReadOnlyDictionary<string, string?> MapAirline(JsonElement root)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in VerificationCheckedColumns.GetColumns(VerificationEntityType.Airline))
            {
                values[column.Name] = column.Name switch
                {
                    nameof(Airline.Iata) => GetString(root, "iata"),
                    nameof(Airline.Icao) => GetString(root, "icao"),
                    nameof(Airline.Name) => GetString(root, "name"),
                    // Airline.CountryCode is deliberately not a checked column (R1) -- AeroAPI's
                    // `country` here is a name ("United Kingdom"), never a code, and
                    // VerificationCheckedColumns.GetColumns(Airline) no longer lists it, so this
                    // switch is never asked to map it.
                    _ => throw new InvalidOperationException(
                        $"{SourceName} has no mapping for checked Airline column '{column.Name}'.")
                };
            }

            return values;
        }

        private static string? GetString(JsonElement root, string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>The property's raw JSON number text, unmodified -- e.g. <c>1742</c>, not a
        /// reformatted or unit-converted value. <see cref="VerificationComparer"/> compares numbers
        /// by parsed value and tolerance, not by string equality, so the exact literal is fine.</summary>
        private static string? GetRawNumber(JsonElement root, string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetRawText()
                : null;

        // Fixed-category error text (R3): a status/fact this code controls, never an upstream
        // exception's message -- matching VerificationSourceDownloader's convention for the two
        // free sources beside this one.
        internal static string HttpStatusError(HttpStatusCode status) => $"{SourceName} lookup failed: HTTP {(int)status}";
        internal static string NetworkError => $"Failed to reach {SourceName} (network error)";
        internal static string TimedOutError => $"Timed out calling {SourceName}";
        internal static string ParseError => $"Could not parse the {SourceName} response";
    }
}
