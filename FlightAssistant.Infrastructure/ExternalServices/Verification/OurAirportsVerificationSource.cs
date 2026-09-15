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
    /// (<see cref="CsvReader.GetRecordsAsync{T}(CancellationToken)"/>) rather than buffered whole --
    /// the full response body is the only thing held besides the two resulting index dictionaries.
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

        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(24);
        private const string ClosedType = "closed";

        private readonly HttpClient _http;
        private readonly ILogger<OurAirportsVerificationSource> _logger;
        private readonly VerificationDatasetCache<Dataset> _cache;

        public OurAirportsVerificationSource(HttpClient http, TimeProvider timeProvider, ILogger<OurAirportsVerificationSource> logger)
        {
            _http = http;
            _logger = logger;
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
                _logger.LogWarning(ex, "OurAirports verification lookup failed: {Message}", ex.Message);
                return VerificationSourceFetchResult.SourceError(ex.Message);
            }

            Row? row = null;
            if (!string.IsNullOrWhiteSpace(key.Iata) && dataset.ByIata.TryGetValue(key.Iata, out var byIata))
                row = byIata;
            else if (!string.IsNullOrWhiteSpace(key.Icao) && dataset.ByIcao.TryGetValue(key.Icao, out var byIcao))
                row = byIcao;

            return row is null ? VerificationSourceFetchResult.NotFound : VerificationSourceFetchResult.Found(ToValues(row));
        }

        public void ReleaseCache() => _cache.Release();

        private async Task<Dataset> DownloadAsync(CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(DatasetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient.Timeout elapsed rather than the caller cancelling -- a source error,
                // not a cancelled operation (which is left to propagate below).
                throw new VerificationSourceFetchException($"Timed out downloading the {SourceName} dataset.");
            }
            catch (HttpRequestException ex)
            {
                throw new VerificationSourceFetchException($"Could not reach {SourceName}: {ex.Message}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // Any non-success -- including 429 and a 404 on the dataset URL itself -- is a
                    // source error, never treated as "no data" (that is what an empty index means).
                    throw new VerificationSourceFetchException(
                        $"{SourceName} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                try
                {
                    return await ParseAsync(response, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new VerificationSourceFetchException($"Could not parse the {SourceName} dataset: {ex.Message}", ex);
                }
            }
        }

        private static async Task<Dataset> ParseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var config = new CsvConfiguration(CultureInfo.InvariantCulture);
            using var csv = new CsvReader(reader, config);
            csv.Context.TypeConverterOptionsCache.AddOptions<string>(
                new CsvHelper.TypeConversion.TypeConverterOptions { NullValues = { "" } });

            var byIata = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            var byIcao = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);

            await foreach (var record in csv.GetRecordsAsync<CsvRow>(ct))
            {
                if (string.Equals(record.Type, ClosedType, StringComparison.OrdinalIgnoreCase)) continue;

                var row = new Row(
                    record.Iata, record.Icao, record.Name, record.Municipality, record.IsoCountry,
                    record.Latitude, record.Longitude, record.ElevationFt, ParseScore(record.Score));

                if (!string.IsNullOrWhiteSpace(row.Iata))
                    KeepHighestScore(byIata, row.Iata, row);

                if (!string.IsNullOrWhiteSpace(row.Icao))
                    KeepHighestScore(byIcao, row.Icao, row);
            }

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
