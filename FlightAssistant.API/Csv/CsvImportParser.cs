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
