using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Interfaces.Verification
{
    /// <summary>
    /// The identifying fields a source lookup needs for one entity, independent of which source
    /// answers it (#367 task 3, spec §4.3). The processor (Task 4) has the loaded entity and builds
    /// one of these from it; a source reads only the fields its own entity type needs and ignores
    /// the rest -- e.g. <c>MledozeCountriesVerificationSource</c> never looks at
    /// <see cref="Iata"/>/<see cref="Icao"/>. A plain key rather than the entity itself, because a
    /// source has no business seeing -- or depending on the shape of -- <c>Airport</c>/<c>Country</c>
    /// beyond the few fields it actually matches rows by; the checked-column comparison itself
    /// (every other column) happens later, in <see cref="VerificationComparer"/>, against the values
    /// <see cref="VerificationSourceFetchResult.Found"/> returns.
    /// </summary>
    /// <param name="Iata">The entity's IATA code. Read by an airport (and, later, airline) source
    /// such as <c>OurAirportsVerificationSource</c>, which tries this first.</param>
    /// <param name="Icao">The entity's ICAO code. Read by the same sources as a fallback when
    /// <see cref="Iata"/> has no match, or is itself empty.</param>
    /// <param name="CountryCode">The country's two-letter code. Read by
    /// <c>MledozeCountriesVerificationSource</c>.</param>
    public sealed record VerificationSourceLookupKey(string? Iata, string? Icao, string? CountryCode)
    {
        /// <summary>A key for an airport (or airline) lookup by IATA/ICAO code.</summary>
        public static VerificationSourceLookupKey ForAirport(string? iata, string? icao) => new(iata, icao, null);

        /// <summary>A key for a country lookup by its two-letter code.</summary>
        public static VerificationSourceLookupKey ForCountry(string countryCode) => new(null, null, countryCode);
    }

    /// <summary>
    /// One source's answer to one <see cref="IVerificationSource.LookupAsync"/> call: found, not
    /// found, or a source error.
    ///
    /// <para>
    /// <b>Ownership (#367 task boundary).</b> Wraps Task 2's <see cref="VerificationSourceLookupResult"/>
    /// (<see cref="VerificationSourceLookupResult.Found"/>/<see cref="VerificationSourceLookupResult.NotFound"/>)
    /// and adds the error case that type's own documentation says is deliberately not modelled
    /// there -- an error is not a lookup result. <see cref="VerificationComparer.Compare"/> never
    /// sees this type or an error case: by the time a request reaches the comparer, every source it
    /// names has already answered <see cref="Lookup"/> (spec §4.2's Job outcomes table) --
    /// <see cref="IsError"/> sends the whole request back for retry before the comparer ever runs.
    /// </para>
    /// </summary>
    public sealed class VerificationSourceFetchResult
    {
        /// <summary>The Task 2 lookup result (Found/NotFound), or null when <see cref="IsError"/>.</summary>
        public VerificationSourceLookupResult? Lookup { get; }

        /// <summary>
        /// A retryable source error -- the dataset itself could not be loaded or read: a timeout, a
        /// non-success HTTP status (5xx, 429, and a 404 on the dataset URL, which is a
        /// misconfiguration, not "no data"), a network failure, or content that could not be
        /// parsed. Null unless <see cref="IsError"/>. Stored on the request's <c>error</c> column,
        /// which admins see in every environment, so it is a fixed category plus facts the source
        /// controls (e.g. an HTTP status code), never an upstream exception's message; the source
        /// logs the detail instead.
        /// </summary>
        public string? Error { get; }

        /// <summary>True when the source could not answer at all (see <see cref="Error"/>) rather
        /// than answering Found or NotFound. Task 4's processor treats this as retryable: attempts
        /// +1, backoff, eventually <c>Error</c> at <c>MaxAttempts</c> (spec §4.2).</summary>
        public bool IsError => Error is not null;

        private VerificationSourceFetchResult(VerificationSourceLookupResult? lookup, string? error)
        {
            Lookup = lookup;
            Error = error;
        }

        /// <summary>The source's dataset has a row for this entity. <paramref name="values"/> is
        /// keyed by the checked column names from <see cref="Application.Verification.VerificationCheckedColumns.GetColumns"/>
        /// for the looked-up entity type -- see <see cref="VerificationSourceLookupResult.Found"/>
        /// for how a value the source could not populate should be represented (null or empty, not
        /// omitted).</summary>
        public static VerificationSourceFetchResult Found(IReadOnlyDictionary<string, string?> values) =>
            new(VerificationSourceLookupResult.Found(values), null);

        /// <summary>The source's dataset loaded but has no row for this entity.</summary>
        public static readonly VerificationSourceFetchResult NotFound = new(VerificationSourceLookupResult.NotFound, null);

        /// <summary>The source could not answer. <paramref name="message"/> must be non-empty.</summary>
        public static VerificationSourceFetchResult SourceError(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            return new VerificationSourceFetchResult(null, message);
        }
    }

    /// <summary>
    /// One outside verification source (#367 task 3, spec §4.3): OurAirports for airports, mledoze
    /// for countries today; a future AeroAPI source (step 9) would implement this the same way for
    /// airports and airlines. Implementations live in Infrastructure --
    /// <c>OurAirportsVerificationSource</c>, <c>MledozeCountriesVerificationSource</c> -- and are
    /// resolved by name through <see cref="IVerificationSourceRegistry"/>.
    ///
    /// <para>
    /// <b>Lifecycle (spec §4.3).</b> A source loads its dataset lazily, on the first
    /// <see cref="LookupAsync"/> call that needs it -- never at construction or DI-resolution time.
    /// The loaded dataset is reused for up to 24 hours, and dropped by <see cref="ReleaseCache"/>
    /// once the registry determines no open request uses this source any more and no lookup has used
    /// the dataset for the job's idle period (<see cref="IVerificationJobOptions.IdleRelease"/>). Implementations
    /// share one dataset-cache helper (Infrastructure) for this download/age/release bookkeeping,
    /// so it exists in one place rather than once per source.
    /// </para>
    /// </summary>
    public interface IVerificationSource
    {
        /// <summary>The source's name as stored in <c>verification_requests.sources</c> and
        /// <c>IVerifiable.VerificationSource</c> (e.g. <c>ourairports</c>, <c>mledoze</c>), and the
        /// name <see cref="IVerificationSourceRegistry.TryGet"/> resolves this instance by.</summary>
        string Name { get; }

        /// <summary>The cost of one row lookup against this source, in USD. 0 for both free sources
        /// this task adds; a future metered source (AeroAPI, step 9) would report its real per-row
        /// cost here, summed into <c>VerificationRequest.EstimatedCostUsd</c> by the queue.</summary>
        decimal CostPerRowUsd { get; }

        /// <summary>Which entity types this source can look up. <see cref="LookupAsync"/> rejects
        /// any other <see cref="VerificationEntityType"/>.</summary>
        IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes { get; }

        /// <summary>
        /// Looks up one entity, loading (or reusing) this source's cached dataset first.
        /// </summary>
        /// <param name="entityType">Must be one of <see cref="SupportedEntityTypes"/>.</param>
        /// <param name="key">The fields this source's row-matching rule needs -- see
        /// <see cref="VerificationSourceLookupKey"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="entityType"/> is not
        /// supported by this source.</exception>
        Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default);

        /// <summary>
        /// Drops this source's cached dataset, if any, so the next <see cref="LookupAsync"/> call
        /// downloads again -- unless a lookup used it less than <paramref name="idleRelease"/> ago, or
        /// a load is in flight. A cached failure is dropped regardless: it keeps only its cooldown.
        /// <see cref="TimeSpan.Zero"/> drops whatever is held. Called by
        /// <see cref="IVerificationSourceRegistry.ReleaseUnused"/> once no open request uses this
        /// source; safe to call when nothing is cached, and safe to call even though this source was
        /// never used this process.
        /// </summary>
        /// <param name="idleRelease">How long the dataset must have gone unused to be dropped. Not negative.</param>
        void ReleaseCache(TimeSpan idleRelease);

        /// <summary>
        /// Whether this source holds anything <see cref="ReleaseCache"/> would drop: a loaded dataset,
        /// a load in flight, or a failed load kept for its cooldown. No I/O, so the job reads it every
        /// cycle to skip asking the queue which sources are in use when nothing is held.
        /// </summary>
        bool HasCachedData { get; }
    }
}
