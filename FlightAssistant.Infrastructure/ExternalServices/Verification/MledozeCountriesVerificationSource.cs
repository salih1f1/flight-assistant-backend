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

        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _http;
        private readonly ILogger<MledozeCountriesVerificationSource> _logger;
        private readonly VerificationDatasetCache<IReadOnlyDictionary<string, Row>> _cache;

        public MledozeCountriesVerificationSource(HttpClient http, TimeProvider timeProvider, ILogger<MledozeCountriesVerificationSource> logger)
        {
            _http = http;
            _logger = logger;
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

        private async Task<IReadOnlyDictionary<string, Row>> DownloadAsync(CancellationToken ct)
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

        private static async Task<IReadOnlyDictionary<string, Row>> ParseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var records = await JsonSerializer.DeserializeAsync<List<JsonRow>>(stream, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new JsonException("The mledoze dataset deserialized to null.");

            var byCode = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Cca2)) continue;

                // Last one wins on a duplicate cca2 -- the real dataset does not contain any; this
                // is simply the natural behaviour of a dictionary assignment, not a chosen rule.
                byCode[record.Cca2] = new Row(record.Cca2, record.Name?.Common, record.Cca3, record.Ccn3, record.Region, record.Subregion);
            }

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
        private sealed record Row(string Cca2, string? Name, string? Cca3, string? Ccn3, string? Region, string? Subregion);
    }
}
