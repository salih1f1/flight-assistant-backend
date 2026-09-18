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

        /// <summary>
        /// The cache key remembering that a source has no such entity. Upper-cased so a caller's
        /// casing cannot produce a second entry for the same code -- <c>POST /flights</c> does not
        /// canonicalise the designator before the lookup.
        /// </summary>
        public static string NegativeCacheKeyFor(VerificationEntityType type, string code) =>
            $"refdata:missing:{type}:{code.Trim().ToUpperInvariant()}";

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
                var disabledFirst = missing.First();
                return ReferenceProvisionResult.NotFound(disabledFirst.Type, disabledFirst.Code);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_options.Budget);

            try
            {
                return await ProvisionCoreAsync(missing, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own budget ran out, not the caller abandoning the request. The dataset download
                // this was waiting on is shared and keeps running, so a retry shortly finds it warm.
                var first = missing.First();
                _logger.LogInformation(
                    "Provisioning {EntityType} '{Code}' exceeded the {Budget} budget.",
                    first.Type, first.Code, _options.Budget);
                return ReferenceProvisionResult.Unavailable(
                    first.Type, first.Code,
                    $"Looking up reference data took longer than {_options.Budget}.");
            }
        }

        /// <summary>
        /// The provisioning walk itself, oblivious to the wall-clock budget -- <see cref="ProvisionAsync"/>
        /// applies <see cref="IReferenceProvisioningOptions.Budget"/> around this call and turns a
        /// timeout into <see cref="ReferenceProvisionStatus.Unavailable"/> rather than letting the
        /// cancellation escape as an exception.
        /// </summary>
        private async Task<ReferenceProvisionResult> ProvisionCoreAsync(
            IReadOnlyCollection<MissingReference> missing, CancellationToken ct)
        {
            using var scope = VerificationJobActor.CreateScope(_scopeFactory);
            var airports = scope.ServiceProvider.GetRequiredService<IAirportRepository>();
            var countries = scope.ServiceProvider.GetRequiredService<ICountryRepository>();
            var airlines = scope.ServiceProvider.GetRequiredService<IAirlineRepository>();

            var createdAirports = new List<Airport>();
            var createdAirlines = new List<Airline>();

            // Case-insensitive: a source may report "gb" where another reports "GB", and the country
            // code is canonicalised by Country.Create anyway.
            var requiredCountryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wroteAnything = false;

            // One loop over every missing entry, dispatched by type -- not a loop per type. A
            // Country entry just contributes its code to the required set and moves on; anything
            // else is fetched here, and the single early-return below (shared by every type) is
            // where the all-or-nothing guarantee lives -- one case per type in the switch below,
            // never a loop per type, which would need its own copy of that return.
            foreach (var entry in missing)
            {
                if (entry.Type == VerificationEntityType.Country)
                {
                    requiredCountryCodes.Add(entry.Code);
                    continue;
                }

                var fetch = await FetchAsync(entry.Type, entry.Code, ct).ConfigureAwait(false);
                if (fetch.Status != FetchStatus.Found)
                    return fetch.ToProvisionResult(entry.Type, entry.Code);

                switch (entry.Type)
                {
                    case VerificationEntityType.Airport:
                    {
                        var airport = BuildAirport(entry.Code, fetch.SourceName!, fetch.Values!);
                        if (airport is null)
                            return ReferenceProvisionResult.NotFound(entry.Type, entry.Code);

                        createdAirports.Add(airport);
                        requiredCountryCodes.Add(airport.CountryCode);
                        break;
                    }

                    case VerificationEntityType.Airline:
                    {
                        // aeroapi reports an operator's country as a name, never a code (#401 R1),
                        // but Airline.CountryCode is a required foreign key -- so the name has to
                        // be resolved before a row can exist at all.
                        var countryName = Value(fetch.Values!, VerificationExtraValues.AirlineCountryName);
                        var resolution = await _countryNames
                            .TryResolveCodeByNameAsync(countryName, ct).ConfigureAwait(false);

                        if (resolution.IsError)
                            return ReferenceProvisionResult.Unavailable(entry.Type, entry.Code, resolution.Error);

                        if (resolution.Code is null)
                        {
                            // No code means no valid row: a data gap we cannot fill, not a
                            // transient failure, so the caller answers 404 rather than 503.
                            _logger.LogInformation(
                                "Cannot provision airline '{Code}': its country '{CountryName}' did not resolve to a code.",
                                entry.Code, countryName);
                            return ReferenceProvisionResult.NotFound(entry.Type, entry.Code);
                        }

                        createdAirlines.Add(BuildAirline(entry.Code, resolution.Code, fetch.SourceName!, fetch.Values!));
                        requiredCountryCodes.Add(resolution.Code);
                        break;
                    }

                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry.Type), entry.Type, "Unknown entity type.");
                }
            }

            // Countries first: both airports.CountryCode and airlines.CountryCode are foreign keys
            // into countries, and the country a row needs is only known once its source (or, for an
            // airline, the name resolver) has answered.
            var countryFailure = await EnsureCountriesAsync(
                countries, requiredCountryCodes, () => wroteAnything = true, ct).ConfigureAwait(false);
            if (countryFailure is not null)
                return countryFailure;

            foreach (var airport in createdAirports)
            {
                await airports.AddAsync(airport, ct).ConfigureAwait(false);
                wroteAnything = true;
            }

            foreach (var airline in createdAirlines)
            {
                await airlines.AddAsync(airline, ct).ConfigureAwait(false);
                wroteAnything = true;
            }

            // Every repository resolved from this scope shares its DbContext, so one save commits
            // every row in a single transaction, and EF orders the INSERTs by the foreign-key graph.
            // ReferenceProvisioningPostgresTests (a later task) proves that ordering against real
            // PostgreSQL, which the InMemory provider cannot (it enforces no foreign key at all).
            // Saved via `airports`, not `countries`: interchangeable in production since both wrap
            // the same DbContext, but the happy-path regression test's `_airports` double is the
            // one that actually observes this call.
            if (wroteAnything)
            {
                try
                {
                    await airports.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Application cannot see EF Core's DbUpdateException -- that boundary is
                    // deliberate (#465 review: worth more than eliminating a rare 500, and
                    // translating it inside AirportRepository would change error behaviour for
                    // every other caller of these repositories, including the admin create/update
                    // paths) -- so any non-cancellation save failure is treated the same way: a
                    // save failure means we could not write the reference data, so we could not
                    // provision, whatever the cause. The concrete case is the concurrent-duplicate
                    // race: Airport.Iata and Airline.Iata both carry unique indexes, so two
                    // POST /flights requests provisioning the same new row race -- one wins, and
                    // the other's save throws here. A logged 503 whose retry succeeds immediately
                    // -- the row the other request just created is now there -- beats an unhandled
                    // 500 on a traveler-facing path. The message is a fixed string, never
                    // ex.Message: DiagnosticDetail is client-visible outside Production and must
                    // never carry a host, constraint or table name.
                    _logger.LogError(ex, "Saving provisioned reference data failed.");
                    var first = missing.First();
                    return ReferenceProvisionResult.Unavailable(
                        first.Type, first.Code, "Saving the provisioned reference data failed.");
                }
            }

            return ReferenceProvisionResult.Provisioned;
        }

        /// <summary>
        /// Creates any of <paramref name="codes"/> that <c>countries</c> does not already hold.
        /// Returns null when every code is now present, or the failure that stopped it. One batched
        /// read for the whole set, then at most one source lookup per genuinely missing code -- so
        /// two airports in the same country cost one lookup, not two.
        /// </summary>
        private async Task<ReferenceProvisionResult?> EnsureCountriesAsync(
            ICountryRepository countries,
            IReadOnlyCollection<string> codes,
            Action markWrote,
            CancellationToken ct)
        {
            if (codes.Count == 0)
                return null;

            var existing = await countries.FindByCodesAsync(codes, ct).ConfigureAwait(false);
            var present = existing.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var code in codes)
            {
                if (present.Contains(code))
                    continue;

                var fetch = await FetchAsync(VerificationEntityType.Country, code, ct).ConfigureAwait(false);
                if (fetch.Status != FetchStatus.Found)
                    return fetch.ToProvisionResult(VerificationEntityType.Country, code);

                await countries.AddAsync(BuildCountry(code, fetch.SourceName!, fetch.Values!), ct).ConfigureAwait(false);
                markWrote();
            }

            return null;
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

        /// <summary>
        /// Builds the row once the country name has already resolved to a code -- the caller
        /// handles the null-code case (no valid row can exist) before this is ever invoked, so
        /// unlike <see cref="BuildAirport"/> this never itself returns null.
        /// </summary>
        private static Airline BuildAirline(
            string requestedIata,
            string countryCode,
            string sourceName,
            IReadOnlyDictionary<string, string?> values)
        {
            var airline = Airline.Create(
                iata: Value(values, nameof(Airline.Iata)) ?? requestedIata,
                icao: Value(values, nameof(Airline.Icao)),
                name: Value(values, nameof(Airline.Name)) ?? requestedIata,
                countryCode: countryCode);

            Stamp(airline, sourceName);
            return airline;
        }

        private static Country BuildCountry(
            string requestedCode, string sourceName, IReadOnlyDictionary<string, string?> values)
        {
            var country = Country.Create(
                code: Value(values, nameof(Country.Code)) ?? requestedCode,
                name: Value(values, nameof(Country.Name)) ?? requestedCode,
                alpha3: Value(values, nameof(Country.Alpha3)),
                numericCode: ParseInt(Value(values, nameof(Country.NumericCode))),
                region: Value(values, nameof(Country.Region)),
                subregion: Value(values, nameof(Country.Subregion)));

            Stamp(country, sourceName);
            return country;
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
        /// <see cref="FetchFromSourcesAsync"/>, with the negative cache in front of it: a code a
        /// source has already said it does not have costs nothing to ask about again. Only a miss is
        /// remembered -- never a hit, and never a failure, which would turn a brief outage into
        /// hours of 404s. A cache outage fails open, exactly as the manual-sync cooldown does (#369).
        /// </summary>
        private async Task<Fetch> FetchAsync(VerificationEntityType type, string code, CancellationToken ct)
        {
            var ttl = _options.NegativeCacheTtl;
            var cacheKey = NegativeCacheKeyFor(type, code);

            if (ttl > TimeSpan.Zero)
            {
                try
                {
                    if (await _cache.GetAsync<string>(cacheKey, ct).ConfigureAwait(false) is not null)
                        return Fetch.NotFound;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fail open: a cache outage must not stop a provision that would have succeeded.
                    _logger.LogWarning(ex, "Reading the reference-provisioning negative cache failed for {CacheKey}.", cacheKey);
                }
            }

            var fetch = await FetchFromSourcesAsync(type, code, ct).ConfigureAwait(false);

            if (fetch.Status == FetchStatus.NotFound && ttl > TimeSpan.Zero)
            {
                try
                {
                    await _cache.SetAsync(cacheKey, "1", ttl, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Writing the reference-provisioning negative cache failed for {CacheKey}.", cacheKey);
                }
            }

            return fetch;
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
        private async Task<Fetch> FetchFromSourcesAsync(VerificationEntityType type, string code, CancellationToken ct)
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

                // Only a source that actually bills is gated. From SoftLimit onward, matching
                // AeroApiFlightSearchBudgetDecorator -- the closest analogue, also traveler-facing
                // and also discretionary spend -- rather than #401's Exhausted-only rule, which is
                // for work an admin explicitly asked for. Unavailable, never NotFound: a refusal
                // teaches us nothing about whether the entity exists.
                if (source.CostPerRowUsd > 0m)
                {
                    var budgetState = await _budget.GetStateAsync(ct).ConfigureAwait(false);
                    if (budgetState != ProviderBudgetState.Normal)
                    {
                        _logger.LogInformation(
                            "Not provisioning {EntityType} '{Code}' from metered source {SourceName}: budget is {BudgetState}.",
                            type, code, sourceName, budgetState);
                        return Fetch.Unavailable(
                            $"The flight data budget is {budgetState}, so '{sourceName}' was not called.");
                    }
                }

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
