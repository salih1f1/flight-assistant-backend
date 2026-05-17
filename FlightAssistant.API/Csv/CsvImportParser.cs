using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System.Globalization;
using System.Text.Json;

namespace FlightAssistant.API.Csv;

public static class CsvImportParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<IReadOnlyList<T>> ParseAsync<T, TMap>(Stream stream)
        where TMap : ClassMap<T>, new()
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        // Treat empty CSV cells as null for string and nullable value types
        csv.Context.TypeConverterOptionsCache.AddOptions<string>(
            new TypeConverterOptions { NullValues = { "" } });
        csv.Context.RegisterClassMap<TMap>();
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
            return JsonSerializer.Deserialize(json, _targetType, JsonOpts);
        }

        public override string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
            => value is null ? "[]" : JsonSerializer.Serialize(value, JsonOpts);
    }
}
