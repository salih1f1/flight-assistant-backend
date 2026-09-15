using System.Text.Json;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>
    /// Verification source for countries against the free mledoze/countries dataset
    /// (<c>https://raw.githubusercontent.com/mledoze/countries/master/countries.json</c>, ~1.4 MB,
    /// ~250 records) -- #367 task 3, spec §4.3.
    ///
    /// <para>
    /// <b>Why mledoze, not restcountries.</b> restcountries v3.1 (the source the original spec
    /// named) was shut down; v5 needs an API key and has a monthly quota. mledoze/countries is the
    /// dataset restcountries itself was built from, still maintained, and needs neither. Licensed
    /// ODbL-1.0 -- attribution is added in #367 task 5.
    /// </para>
    ///
    /// <para>
    /// <b>Lookup (controller ruling).</b> By <c>cca2</c>, case-insensitively. <see cref="Country.Name"/>
    /// maps from <c>name.common</c> -- not <c>name.official</c>, which is a longer, more formal
    /// string that a plain-common-name checked column would flag as a mismatch on every well-formed
    /// row (e.g. common <c>"Turkey"</c> vs. official <c>"Republic of Türkiye"</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Empty dataset vs. one bad record (fix round 1, item 6; revised in fix round 2, item 8).</b>
    /// A dataset that yields <b>zero</b> indexed records -- an empty body, <c>[]</c>, or a renamed
    /// <c>cca2</c>/<c>name.common</c> field on <i>every</i> record -- is a source error, never "not
    /// found" for every country: <c>NotFound</c> means the dataset loaded and has records, just not
    /// this one, and Task 4 treats <c>NotFound</c> as terminal (it flags the entity for attention),
    /// so this source must not look like that for every single lookup just because parsing broke.
    /// A <i>single</i> record missing <c>cca2</c> or <c>name.common</c>, by contrast, is
    /// <b>skipped</b> (logged as a warning naming its index and <c>cca3</c>, if present) rather than
    /// failing the whole dataset -- round 1 originally failed the whole dataset on one bad record,
    /// which meant one malformed upstream record turned every country's verification into a source
    /// error that keeps retrying (spec §4.2's backoff) and never clears. The skipped country instead
    /// simply looks up as <c>NotFound</c>, which is visible to an admin and does not block every
    /// other country.
    /// </para>
    /// </summary>
    public sealed class MledozeCountriesVerificationSource : IVerificationSource
    {
        /// <summary>The name this source is registered and resolved under, and the value recorded
        /// in <c>verification_requests.sources</c> / <c>IVerifiable.VerificationSource</c>.</summary>
        public const string SourceName = "mledoze";

        /// <summary>Named <see cref="HttpClient"/> this source is registered against in
        /// <c>DependencyInjection.AddInfrastructure</c>.</summary>
        public const string HttpClientName = "VerificationMledoze";

        /// <summary>The dataset URL (spec §4.3). No API key, no query string.</summary>
        public const string DatasetUrl = "https://raw.githubusercontent.com/mledoze/countries/master/countries.json";

        /// <summary>Bounds the whole download -- send and body/parse alike (fix round 1, item 1).</summary>
        public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MledozeCountriesVerificationSource> _logger;
        private readonly VerificationDatasetCache<IReadOnlyDictionary<string, Row>> _cache;
        private readonly TimeSpan _downloadTimeout;

        /// <param name="downloadTimeout">Overrides <see cref="DownloadTimeout"/> -- for tests only,
        /// so a timeout/stalled-download test does not have to wait out the real 30-second budget.
        /// DI never passes this; production always uses the named <see cref="DownloadTimeout"/>.</param>
        public MledozeCountriesVerificationSource(
            IHttpClientFactory httpClientFactory, TimeProvider timeProvider, ILogger<MledozeCountriesVerificationSource> logger,
            TimeSpan? downloadTimeout = null)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _downloadTimeout = downloadTimeout ?? DownloadTimeout;
            _cache = new VerificationDatasetCache<IReadOnlyDictionary<string, Row>>(timeProvider, MaxCacheAge);
        }

        public string Name => SourceName;

        /// <summary>Free.</summary>
        public decimal CostPerRowUsd => 0m;

        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes { get; } =
            new[] { VerificationEntityType.Country };

        public async Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            if (!SupportedEntityTypes.Contains(entityType))
                throw new ArgumentOutOfRangeException(nameof(entityType), entityType, $"{SourceName} does not support {entityType}.");

            IReadOnlyDictionary<string, Row> byCode;
            try
            {
                byCode = await _cache.GetAsync(DownloadAsync, ct).ConfigureAwait(false);
            }
            catch (VerificationSourceFetchException ex)
            {
                _logger.LogWarning(ex, "mledoze verification lookup failed: {Message}", ex.Message);
                return VerificationSourceFetchResult.SourceError(ex.Message);
            }

            if (string.IsNullOrWhiteSpace(key.CountryCode) || !byCode.TryGetValue(key.CountryCode, out var row))
                return VerificationSourceFetchResult.NotFound;

            return VerificationSourceFetchResult.Found(ToValues(row));
        }

        public void ReleaseCache() => _cache.Release();

        private Task<IReadOnlyDictionary<string, Row>> DownloadAsync(CancellationToken ct) =>
            VerificationSourceDownloader.DownloadAsync(
                _httpClientFactory, HttpClientName, DatasetUrl, SourceName, _downloadTimeout, ParseAsync, ct);

        /// <summary>Instance method (not static) so it can log a warning per skipped record via
        /// <see cref="_logger"/> -- fix round 2, item 8.</summary>
        private async Task<IReadOnlyDictionary<string, Row>> ParseAsync(Stream stream, CancellationToken ct)
        {
            var records = await JsonSerializer.DeserializeAsync<List<JsonRow>>(stream, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new JsonException("The mledoze dataset deserialized to null.");

            var byCode = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];

                // Fix round 2, item 8 (controller ruling): a record missing either field is
                // skipped, not a whole-dataset failure -- see the type's "Empty dataset vs. one bad
                // record" remark. Logged so the bad upstream record is visible, but the country
                // just looks up as NotFound rather than blocking every other country.
                if (string.IsNullOrWhiteSpace(record.Cca2) || string.IsNullOrWhiteSpace(record.Name?.Common))
                {
                    _logger.LogWarning(
                        "Skipping a {SourceName} record missing cca2 or name.common (index {Index}, cca3 {Cca3}).",
                        SourceName, index, record.Cca3 ?? "<none>");
                    continue;
                }

                // Last one wins on a duplicate cca2 -- the real dataset does not contain any; this
                // is simply the natural behaviour of a dictionary assignment, not a chosen rule.
                byCode[record.Cca2] = new Row(record.Cca2, record.Name.Common, record.Cca3, record.Ccn3, record.Region, record.Subregion);
            }

            // Zero *surviving* records -- an empty body, [], or every record being bad -- is the
            // one case that is still a source error (see the type's class doc remark).
            if (byCode.Count == 0)
                throw new VerificationSourceFetchException($"The {SourceName} dataset had no usable rows.");

            return byCode;
        }

        private static IReadOnlyDictionary<string, string?> ToValues(Row row)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in VerificationCheckedColumns.GetColumns(VerificationEntityType.Country))
            {
                values[column.Name] = column.Name switch
                {
                    nameof(Country.Code) => row.Cca2,
                    nameof(Country.Name) => row.Name,
                    nameof(Country.Alpha3) => row.Cca3,
                    nameof(Country.NumericCode) => row.Ccn3,
                    nameof(Country.Region) => row.Region,
                    nameof(Country.Subregion) => row.Subregion,
                    _ => throw new InvalidOperationException(
                        $"{SourceName} has no mapping for checked column '{column.Name}'.")
                };
            }

            return values;
        }

        /// <summary>The JSON shape mledoze's <c>countries.json</c> uses for one country -- only the
        /// fields this source reads; every other real field (currencies, idd, capital, flags, ...)
        /// is ignored by <see cref="JsonSerializer"/> without needing to be declared here.</summary>
        private sealed class JsonRow
        {
            public string? Cca2 { get; set; }
            public string? Cca3 { get; set; }
            public string? Ccn3 { get; set; }
            public string? Region { get; set; }
            public string? Subregion { get; set; }
            public JsonNameRow? Name { get; set; }
        }

        private sealed class JsonNameRow
        {
            public string? Common { get; set; }
        }

        /// <summary>One country's checked-column values, kept in memory after parsing.</summary>
        private sealed record Row(string Cca2, string Name, string? Cca3, string? Ccn3, string? Region, string? Subregion);
    }
}
