using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using FlightAssistant.Domain.Common.Exceptions;
using System.Globalization;
using System.Text.Json;

namespace FlightAssistant.API.Csv;

public static class CsvImportParser
{
    /// <summary>
    /// Style used to parse the <c>VerifiedAt</c> import column (#362): without
    /// <see cref="DateTimeStyles.AdjustToUniversal"/>/<see cref="DateTimeStyles.AssumeUniversal"/>,
    /// CsvHelper's default <see cref="DateTime"/> parsing can yield
    /// <see cref="DateTimeKind.Unspecified"/> — the bug class CLAUDE.md documents for date query
    /// parameters, which Npgsql refuses against a <c>timestamptz</c> column, applies equally here.
    /// </summary>
    private const DateTimeStyles VerifiedAtStyles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;

    /// <summary>
    /// Maps the three optional verification columns (#362) shared by every import DTO —
    /// <c>VerifiedAt</c>/<c>VerifiedBy</c>/<c>VerificationSource</c> — by constructor
    /// <see cref="ClassMap.Parameter(string)"/> rather than <see cref="ClassMap{T}.Map"/>.
    /// Every import DTO is a positional record with no parameterless constructor, so CsvHelper
    /// (including its own <c>AutoMap</c>) always builds it via the constructor: a property
    /// <c>Map</c> configures a member CsvHelper never actually reads from when constructing the
    /// object, leaving the AutoMap-generated, non-optional <see cref="ClassMap.Parameter"/> for
    /// the same name in force — which is exactly why an older CSV without these columns kept
    /// failing header validation until this switched to re-mapping the <em>parameter</em>. Each
    /// <c>Parameter(name)</c> call here replaces that DTO's own AutoMap-registered parameter for
    /// the same name (CsvHelper matches by name, not by call site), so it is marked
    /// <c>Optional()</c> — a CSV without them (an older export) still parses, the columns are
    /// simply left null — and <c>VerifiedAt</c> is parsed with UTC styles.
    /// </summary>
    public static void MapImportVerificationParameters<T>(this ClassMap<T> map)
    {
        map.Parameter("VerifiedAt").Name("VerifiedAt").TypeConverterOption.DateTimeStyles(VerifiedAtStyles).Optional();
        map.Parameter("VerifiedBy").Name("VerifiedBy").Optional();
        map.Parameter("VerificationSource").Name("VerificationSource").Optional();
    }

    /// <summary>
    /// Parses a CSV into <typeparamref name="T"/> records via CsvHelper AutoMap.
    /// </summary>
    /// <param name="rejectHeaders">
    /// Header names that must NOT be present in the CSV. Used to give a clear, actionable error
    /// when a column has been dropped from the import contract (e.g. Airports' "Timezone") instead
    /// of silently ignoring it — an admin re-importing a stale export should be told, not left
    /// wondering why the column had no effect.
    /// </param>
    public static async Task<IReadOnlyList<T>> ParseAsync<T, TMap>(Stream stream, IReadOnlyCollection<string>? rejectHeaders = null)
        where TMap : ClassMap<T>, new()
    {
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        // Treat empty CSV cells as null for string and nullable value types
        csv.Context.TypeConverterOptionsCache.AddOptions<string>(
            new TypeConverterOptions { NullValues = { "" } });
        csv.Context.RegisterClassMap<TMap>();

        if (rejectHeaders is { Count: > 0 })
        {
            await csv.ReadAsync();
            csv.ReadHeader();
            var stale = csv.HeaderRecord?.FirstOrDefault(h => rejectHeaders.Contains(h, StringComparer.OrdinalIgnoreCase));
            if (stale is not null)
            {
                throw new DomainException(
                    $"This CSV has a '{stale}' column that is no longer part of the import contract. Remove that column and re-import.",
                    400);
            }
        }

        var records = new List<T>();
        await foreach (var r in csv.GetRecordsAsync<T>())
            records.Add(r);
        return records;
    }

    public static JsonListConverter JsonListOf(Type targetType) => new(targetType);

    public sealed class JsonListConverter : DefaultTypeConverter
    {
        private readonly Type _targetType;

        public JsonListConverter(Type targetType)
        {
            _targetType = targetType;
        }

        public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
        {
            var json = string.IsNullOrWhiteSpace(text) ? "[]" : text;
            try
            {
                return JsonSerializer.Deserialize(json, _targetType, CsvJsonOptions.Default);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new CsvHelper.TypeConversion.TypeConverterException(this, memberMapData, text, row.Context, ex.Message);
            }
        }

        public override string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
            => value is null ? "[]" : JsonSerializer.Serialize(value, CsvJsonOptions.Default);
    }
}
