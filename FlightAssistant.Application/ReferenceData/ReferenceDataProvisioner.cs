using System.Globalization;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.ReferenceData
{
    /// <summary>
    /// Creates reference rows from the verification sources (#465). See
    /// <see cref="IReferenceDataProvisioner"/> for what it promises; this type owns how.
    ///
    /// <para>
    /// <b>Its own DI scope.</b> Writes go through <see cref="VerificationJobActor.CreateScope"/>,
    /// not the caller's scope. <c>AdminAuditInterceptor</c> audits every tracked change in a
    /// <c>SaveChanges</c> that carries an actor, so setting one on a traveler's request scope would
    /// start auditing that traveler's own flight writes -- which must stay untouched. A separate
    /// scope audits the reference writes under <c>system</c> and leaves the traveler's write alone.
    /// It passes no correlation id: the request's id lives in <c>HttpContext.Items</c>, invisible to
    /// Application, so these rows fall back to <c>Activity.Current?.Id</c> like any actor-less path.
    /// </para>
    ///
    /// <para>
    /// <b>It never throws for an expected failure.</b> A miss, a source error, a budget refusal and
    /// a deadline are statuses, because this runs inside a traveler's <c>POST /flights</c> and the
    /// caller has to turn each into a different status code.
    /// </para>
    /// </summary>
    public sealed class ReferenceDataProvisioner : IReferenceDataProvisioner
    {
        /// <summary><c>ISeedableEntity.SourceType</c> for a row this service created -- distinct from
        /// <c>seed</c>, <c>admin</c> and <c>import</c>, so an operator can tell where it came from.
        /// The <c>Create</c> factories hardcode <c>"admin"</c>, so it is assigned afterwards.</summary>
        public const string ProvisionedSourceType = "auto";

        private readonly IVerificationSourceRegistry _registry;
        private readonly IVerificationJobOptions _verificationOptions;
        private readonly ICountryNameResolver _countryNames;
        private readonly ICacheService _cache;
        private readonly IProviderBudgetGuard _budget;
        private readonly IReferenceProvisioningOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReferenceDataProvisioner> _logger;

        public ReferenceDataProvisioner(
            IVerificationSourceRegistry registry,
            IVerificationJobOptions verificationOptions,
            ICountryNameResolver countryNames,
            ICacheService cache,
            IProviderBudgetGuard budget,
            IReferenceProvisioningOptions options,
            IServiceScopeFactory scopeFactory,
            ILogger<ReferenceDataProvisioner> logger)
        {
            _registry = registry;
            _verificationOptions = verificationOptions;
            _countryNames = countryNames;
            _cache = cache;
            _budget = budget;
            _options = options;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<ReferenceProvisionResult> ProvisionAsync(
            IReadOnlyCollection<MissingReference> missing, CancellationToken ct = default)
        {
            if (missing.Count == 0)
                return ReferenceProvisionResult.Provisioned;

            // Disabled behaves exactly like "the source has no such entity", which is today's 404.
            if (!_options.Enabled)
            {
                var first = missing.First();
                return ReferenceProvisionResult.NotFound(first.Type, first.Code);
            }

            using var scope = VerificationJobActor.CreateScope(_scopeFactory);
            var airports = scope.ServiceProvider.GetRequiredService<IAirportRepository>();

            var created = new List<Airport>();
            foreach (var entry in missing.Where(m => m.Type == VerificationEntityType.Airport))
            {
                var fetch = await FetchAsync(entry.Type, entry.Code, ct).ConfigureAwait(false);
                if (fetch.Status != FetchStatus.Found)
                    return fetch.ToProvisionResult(entry.Type, entry.Code);

                var airport = BuildAirport(entry.Code, fetch.SourceName!, fetch.Values!);
                if (airport is null)
                    return ReferenceProvisionResult.NotFound(entry.Type, entry.Code);

                created.Add(airport);
            }

            foreach (var airport in created)
                await airports.AddAsync(airport, ct).ConfigureAwait(false);

            if (created.Count > 0)
                await airports.SaveChangesAsync(ct).ConfigureAwait(false);

            return ReferenceProvisionResult.Provisioned;
        }

        /// <summary>
        /// Builds the row from a Found fetch, or null when the source's row cannot be used --
        /// today that means a blank <see cref="Airport.CountryCode"/>: an airport with no country
        /// cannot satisfy its <c>countries</c> foreign key, so it is genuinely unprovisionable
        /// rather than a crash. Never lets a blank value reach <see cref="Airport.Create"/>, which
        /// calls <c>ToCanonicalCode()</c> on it and throws <see cref="NullReferenceException"/> on
        /// null.
        /// </summary>
        private static Airport? BuildAirport(
            string requestedIata, string sourceName, IReadOnlyDictionary<string, string?> values)
        {
            var countryCode = Value(values, nameof(Airport.CountryCode));
            if (countryCode is null)
                return null;

            var airport = Airport.Create(
                iata: Value(values, nameof(Airport.Iata)) ?? requestedIata,
                icao: Value(values, nameof(Airport.Icao)),
                name: Value(values, nameof(Airport.Name)) ?? requestedIata,
                city: Value(values, nameof(Airport.City)) ?? string.Empty,
                countryCode: countryCode,
                latitude: ParseDouble(Value(values, nameof(Airport.Latitude))),
                longitude: ParseDouble(Value(values, nameof(Airport.Longitude))),
                elevationFt: ParseInt(Value(values, nameof(Airport.ElevationFt))));

            Stamp(airport, sourceName);
            return airport;
        }

        /// <summary>Marks a freshly-created row as verified by the source it came from. The row *is*
        /// that source's data, so it is verified by construction; stamping it also stops the
        /// flight-tracking trigger (#410) re-queueing it for <c>Verification:StaleAfterDays</c>.</summary>
        private static void Stamp(SeedableEntity entity, string sourceName)
        {
            entity.SourceType = ProvisionedSourceType;
            entity.Verify(VerificationJobActor.ActorId, sourceName);
        }

        private static string? Value(IReadOnlyDictionary<string, string?> values, string column) =>
            values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        private static double? ParseDouble(string? value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        private static int? ParseInt(string? value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        private enum FetchStatus { Found, NotFound, Unavailable }

        private sealed record Fetch(
            FetchStatus Status, string? SourceName, IReadOnlyDictionary<string, string?>? Values, string? Error)
        {
            public static readonly Fetch NotFound = new(FetchStatus.NotFound, null, null, null);
            public static Fetch Found(string sourceName, IReadOnlyDictionary<string, string?> values) =>
                new(FetchStatus.Found, sourceName, values, null);
            public static Fetch Unavailable(string? error) => new(FetchStatus.Unavailable, null, null, error);

            public ReferenceProvisionResult ToProvisionResult(VerificationEntityType type, string code) =>
                Status == FetchStatus.NotFound
                    ? ReferenceProvisionResult.NotFound(type, code)
                    : ReferenceProvisionResult.Unavailable(type, code, Error);
        }

        /// <summary>
        /// Asks each source configured for <paramref name="type"/> in turn; the first that answers
        /// Found wins. A source error stops the search rather than falling through to the next
        /// source: falling through would let one broken source's outage be reported as a miss.
        /// If every configured source turns out not to support <paramref name="type"/> at all --
        /// reachable purely by misconfiguration, e.g. <c>Verification:DefaultSources:Airport</c>
        /// naming a country-only source -- that is <c>Unavailable</c>, not <c>NotFound</c>: nothing
        /// ever actually answered, so there is no basis to tell a traveler the airport doesn't exist.
        /// </summary>
        private async Task<Fetch> FetchAsync(VerificationEntityType type, string code, CancellationToken ct)
        {
            var sourceNames = _verificationOptions.DefaultSourcesFor(type);
            if (sourceNames.Count == 0)
                return Fetch.Unavailable($"No source is configured for {type}.");

            var anySourceSupportedType = false;

            foreach (var sourceName in sourceNames)
            {
                var source = _registry.TryGet(sourceName);
                if (source is null)
                    return Fetch.Unavailable($"Source '{sourceName}' is not registered.");

                if (!source.SupportedEntityTypes.Contains(type))
                    continue;

                anySourceSupportedType = true;

                var key = type switch
                {
                    VerificationEntityType.Airport => VerificationSourceLookupKey.ForAirport(code, null),
                    VerificationEntityType.Airline => VerificationSourceLookupKey.ForAirline(code, null),
                    VerificationEntityType.Country => VerificationSourceLookupKey.ForCountry(code),
                    _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown entity type.")
                };

                var result = await source.LookupAsync(type, key, ct).ConfigureAwait(false);
                if (result.IsError)
                {
                    _logger.LogWarning(
                        "Provisioning {EntityType} '{Code}' from {SourceName} failed: {Error}",
                        type, code, sourceName, result.Error);
                    return Fetch.Unavailable(result.Error);
                }

                if (result.Lookup!.IsFound)
                    return Fetch.Found(sourceName, result.Lookup.Values!);
            }

            return anySourceSupportedType
                ? Fetch.NotFound
                : Fetch.Unavailable($"No configured source supports {type}.");
        }
    }
}
