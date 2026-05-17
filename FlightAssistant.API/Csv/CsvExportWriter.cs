using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FlightAssistant.API.Csv;

public static class CsvExportWriter
{

    public static byte[] Write<T, TMap>(IReadOnlyList<T> data)
        where TMap : ClassMap<T>, new()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.Context.RegisterClassMap<TMap>();
        csv.WriteRecords(data);
        writer.Flush();
        return ms.ToArray();
    }

    public static readonly JsonCollectionConverter JsonCollection = new();

    public sealed class JsonCollectionConverter : DefaultTypeConverter
    {
        public override string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
            => value is null ? "[]" : JsonSerializer.Serialize(value, CsvJsonOptions.Default);

        public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
            => throw new NotSupportedException("JsonCollectionConverter is write-only; use JsonListConverter for import.");
    }
}
