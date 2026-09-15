using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>
    /// Verification source for airports against the free OurAirports dataset
    /// (<c>https://ourairports.com/airports.csv</c>, ~16 MB) -- #367 task 3, spec §4.3.
    ///
    /// <para>
    /// <b>Lookup (controller ruling).</b> Rows whose <c>type</c> is <c>closed</c> are dropped while
    /// parsing and never enter either index. A lookup tries <see cref="VerificationSourceLookupKey.Iata"/>
    /// against <c>iata_code</c> first; if that is empty or has no match, it falls back to
    /// <see cref="VerificationSourceLookupKey.Icao"/> against <c>icao_code</c>. When several rows
    /// share a code (OurAirports is not unique on either), the one with the highest <c>score</c>
    /// wins -- resolved while parsing, not by keeping every duplicate in memory.
    /// </para>
    ///
    /// <para>
    /// <b>Memory.</b> Only the 8 checked columns plus <c>type</c> (filter) and <c>score</c>
    /// (dedup) are kept per row, and the CSV is stream-parsed one record at a time
    /// (<see cref="CsvReader.GetRecordsAsync{T}(CancellationToken)"/>) rather than buffered whole:
    /// only the two resulting index dictionaries are held once parsing finishes -- the response
    /// body itself is read incrementally, never buffered in full.
    /// </para>
    ///
    /// <para>
    /// <b>Empty dataset (fix round 1, item 6; wording corrected in fix round 2, item 7).</b> A
    /// response that parses successfully but yields zero indexed rows -- an empty body, or a
    /// syntactically valid header followed by no data rows -- is a source error, not "no data found
    /// for anything". <c>NotFound</c> means the dataset loaded *and* has rows, just not this one;
    /// Task 4 treats <c>NotFound</c> as terminal (it flags the entity for attention), so a silently
    /// empty dataset must never be allowed to look like that for every single lookup.
    /// A <i>renamed</i> <c>iata_code</c>/<c>icao_code</c> (or any other column this source reads) is
    /// a <i>different</i> source error, caught earlier: <see cref="CsvRow"/>'s columns are required
    /// (none are <c>Optional()</c>), so CsvHelper's own header validation throws before any row is
    /// read at all, which the parse-failure catch below wraps as "Could not parse..." -- it never
    /// reaches the zero-rows check.
    /// </para>
    ///
    /// <para>
    /// <b>One malformed row (final fix wave, C2).</b> A row with bad CSV data, a missing field, or
    /// a field count unlike the header's is skipped, not a whole-dataset failure; one warning per
    /// parse gives the count and the first few row numbers. The airport it named just looks up as
    /// <c>NotFound</c>.
    /// </para>
    /// </summary>
    public sealed class OurAirportsVerificationSource : IVerificationSource
    {
        /// <summary>The name this source is registered and resolved under, and the value recorded
        /// in <c>verification_requests.sources</c> / <c>IVerifiable.VerificationSource</c>.</summary>
        public const string SourceName = "ourairports";

        /// <summary>Named <see cref="HttpClient"/> this source is registered against in
        /// <c>DependencyInjection.AddInfrastructure</c>.</summary>
        public const string HttpClientName = "VerificationOurAirports";

        /// <summary>The dataset URL (spec §4.3). No API key, no query string.</summary>
        public const string DatasetUrl = "https://ourairports.com/airports.csv";

        /// <summary>Bounds the whole download -- send and body/parse alike (fix round 1, item 1).
        /// The CSV is ~16 MB, hence minutes rather than seconds.</summary>
        public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

        /// <summary>The largest body this source reads (final fix wave, C5): about four times the
        /// file's ~16 MB today, so it can grow without a code change, while an unbounded or wrong body
        /// fails as a source error instead of being parsed.</summary>
        public const long MaxDownloadBytes = 64L * 1024 * 1024;

        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(24);
        private const string ClosedType = "closed";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OurAirportsVerificationSource> _logger;
        private readonly VerificationDatasetCache<Dataset> _cache;
        private readonly TimeSpan _downloadTimeout;

        /// <param name="downloadTimeout">Overrides <see cref="DownloadTimeout"/> -- for tests only,
        /// so a timeout/stalled-download test does not have to wait out the real 3-minute budget.
        /// DI never passes this; production always uses the named <see cref="DownloadTimeout"/>.</param>
        public OurAirportsVerificationSource(
            IHttpClientFactory httpClientFactory, TimeProvider timeProvider, ILogger<OurAirportsVerificationSource> logger,
            TimeSpan? downloadTimeout = null)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _downloadTimeout = downloadTimeout ?? DownloadTimeout;
            _cache = new VerificationDatasetCache<Dataset>(timeProvider, MaxCacheAge);
        }

        public string Name => SourceName;

        /// <summary>Free.</summary>
        public decimal CostPerRowUsd => 0m;

        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes { get; } =
            new[] { VerificationEntityType.Airport };

        public async Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            if (!SupportedEntityTypes.Contains(entityType))
                throw new ArgumentOutOfRangeException(nameof(entityType), entityType, $"{SourceName} does not support {entityType}.");

            Dataset dataset;
            try
            {
                dataset = await _cache.GetAsync(DownloadAsync, ct).ConfigureAwait(false);
            }
            catch (VerificationSourceFetchException ex)
            {
                // Logged once, with the whole exception, where the load failed (DownloadAsync); a
                // lookup inside the failure cooldown just hands on the same fixed-category error.
                return VerificationSourceFetchResult.SourceError(ex.Message);
            }

            Row? row = null;
            if (!string.IsNullOrWhiteSpace(key.Iata) && dataset.ByIata.TryGetValue(key.Iata, out var byIata))
                row = byIata;
            else if (!string.IsNullOrWhiteSpace(key.Icao) && dataset.ByIcao.TryGetValue(key.Icao, out var byIcao))
                row = byIcao;

            return row is null ? VerificationSourceFetchResult.NotFound : VerificationSourceFetchResult.Found(ToValues(row));
        }

        public void ReleaseCache(TimeSpan idleRelease) => _cache.Release(idleRelease);

        public bool HasCachedData => _cache.HasCachedData;

        private async Task<Dataset> DownloadAsync(CancellationToken ct)
        {
            try
            {
                return await VerificationSourceDownloader.DownloadAsync(
                    _httpClientFactory, HttpClientName, DatasetUrl, SourceName, _downloadTimeout, MaxDownloadBytes, ParseAsync, ct).ConfigureAwait(false);
            }
            catch (VerificationSourceFetchException ex)
            {
                // The stored error is a fixed category (final fix wave, C1); the upstream detail is
                // only in this log line, written once per failed load with the whole exception.
                _logger.LogWarning(ex, "Loading the {SourceName} verification dataset failed: {Error}", SourceName, ex.Message);
                throw;
            }
        }

        /// <summary>How many skipped rows one parse's warning names, by row number.</summary>
        internal const int SkippedSampleSize = 3;

        /// <summary>Instance method (not static) so it can log the skipped rows via <see cref="_logger"/>.</summary>
        private async Task<Dataset> ParseAsync(Stream stream, CancellationToken ct)
        {
            using var reader = new StreamReader(stream);

            // Final fix wave, C2: a malformed row is skipped and counted, not a whole-dataset failure.
            // CsvHelper's defaults throw on bad data (e.g. text after a closing quote) and on a missing
            // field; these callbacks flag the row instead. Header validation keeps its throwing
            // default, so a renamed or missing column still fails the whole parse loudly.
            var rowIsMalformed = false;
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                BadDataFound = _ => rowIsMalformed = true,
                MissingFieldFound = _ => rowIsMalformed = true
            };
            using var csv = new CsvReader(reader, config);
            csv.Context.TypeConverterOptionsCache.AddOptions<string>(
                new CsvHelper.TypeConversion.TypeConverterOptions { NullValues = { "" } });

            var byIata = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            var byIcao = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            var skipped = 0;
            var skippedSample = new List<int>(SkippedSampleSize);

            await foreach (var record in csv.GetRecordsAsync<CsvRow>(ct))
            {
                // The callbacks ran while this row was read and mapped; the next row is read only
                // after this body. A field count unlike the header's is malformed too: an unquoted
                // comma shifts every later column, so iata_code would hold another column's value.
                if (rowIsMalformed || csv.Parser.Count != csv.HeaderRecord!.Length)
                {
                    rowIsMalformed = false;
                    skipped++;
                    if (skippedSample.Count < SkippedSampleSize)
                        skippedSample.Add(csv.Parser.Row);
                    continue;
                }

                if (string.Equals(record.Type, ClosedType, StringComparison.OrdinalIgnoreCase)) continue;

                var row = new Row(
                    record.Iata, record.Icao, record.Name, record.Municipality, record.IsoCountry,
                    record.Latitude, record.Longitude, record.ElevationFt, ParseScore(record.Score));

                if (!string.IsNullOrWhiteSpace(row.Iata))
                    KeepHighestScore(byIata, row.Iata, row);

                if (!string.IsNullOrWhiteSpace(row.Icao))
                    KeepHighestScore(byIcao, row.Icao, row);
            }

            // One warning per parse, like mledoze's skipped records (final fix wave, C2).
            if (skipped > 0)
                _logger.LogWarning(
                    "Skipped {SkippedCount} malformed {SourceName} row(s); first row numbers: {SkippedSample}.",
                    skipped, SourceName, string.Join(", ", skippedSample));

            // Fix round 1, item 6 (wording corrected in fix round 2, item 7): an empty body, or a
            // syntactically valid header with zero data rows, lands here as zero indexed rows -- a
            // source error, never treated as "nothing matches". A renamed iata_code/icao_code
            // column is a *different* source error, caught earlier by CsvHelper's own header
            // validation (see the class doc's "Empty dataset" remark) -- it never reaches this check.
            if (byIata.Count == 0 && byIcao.Count == 0)
                throw new VerificationSourceFetchException(VerificationSourceDownloader.NoUsableRowsError(SourceName));

            return new Dataset(byIata, byIcao);
        }

        private static void KeepHighestScore(Dictionary<string, Row> index, string key, Row row)
        {
            if (!index.TryGetValue(key, out var existing) || row.Score > existing.Score)
                index[key] = row;
        }

        private static double ParseScore(string? raw) =>
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ? score : 0;

        private static IReadOnlyDictionary<string, string?> ToValues(Row row)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in VerificationCheckedColumns.GetColumns(VerificationEntityType.Airport))
            {
                values[column.Name] = column.Name switch
                {
                    nameof(Airport.Iata) => row.Iata,
                    nameof(Airport.Icao) => row.Icao,
                    nameof(Airport.Name) => row.Name,
                    nameof(Airport.City) => row.City,
                    nameof(Airport.CountryCode) => row.CountryCode,
                    nameof(Airport.Latitude) => row.Latitude,
                    nameof(Airport.Longitude) => row.Longitude,
                    nameof(Airport.ElevationFt) => row.ElevationFt,
                    _ => throw new InvalidOperationException(
                        $"{SourceName} has no mapping for checked column '{column.Name}'.")
                };
            }

            return values;
        }

        /// <summary>The CSV columns this source reads, by their real OurAirports header name.
        /// Every other one of the 24 real columns is left unmapped and simply ignored.</summary>
        private sealed class CsvRow
        {
            [Name("type")]
            public string? Type { get; set; }

            [Name("name")]
            public string? Name { get; set; }

            [Name("municipality")]
            public string? Municipality { get; set; }

            [Name("iso_country")]
            public string? IsoCountry { get; set; }

            [Name("latitude_deg")]
            public string? Latitude { get; set; }

            [Name("longitude_deg")]
            public string? Longitude { get; set; }

            [Name("elevation_ft")]
            public string? ElevationFt { get; set; }

            [Name("icao_code")]
            public string? Icao { get; set; }

            [Name("iata_code")]
            public string? Iata { get; set; }

            [Name("score")]
            public string? Score { get; set; }
        }

        /// <summary>One row's checked-column values, kept in memory after parsing -- the raw CSV
        /// row (and every column this source does not check) is discarded once this is built.
        /// <see cref="Score"/> is kept only to resolve duplicates while parsing.</summary>
        private sealed record Row(
            string? Iata, string? Icao, string? Name, string? City, string? CountryCode,
            string? Latitude, string? Longitude, string? ElevationFt, double Score);

        private sealed record Dataset(IReadOnlyDictionary<string, Row> ByIata, IReadOnlyDictionary<string, Row> ByIcao);
    }
}
