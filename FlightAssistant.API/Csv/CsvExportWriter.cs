using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;

namespace FlightAssistant.API.Csv;

public static class CsvExportWriter
{
    /// <summary>
    /// The round-trip ("O") format (#362): <c>2026-09-14T20:31:12.1234567Z</c>. CsvHelper's culture
    /// default drops the offset/kind and truncates precision, which would silently turn every
    /// exported <c>VerifiedAt</c> into a value re-importing can no longer parse back to
    /// <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    private const string VerifiedAtFormat = "O";

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

    /// <summary>
    /// Maps the three verification columns (#362) every export DTO carries, in the fixed order
    /// <c>VerifiedAt</c>, <c>VerifiedBy</c>, <c>VerificationSource</c>, after all of that DTO's own
    /// columns. <c>VerifiedAt</c> is written in the UTC round-trip format so import can parse it
    /// straight back to <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    public static void MapExportVerificationColumns<T>(
        this ClassMap<T> map,
        Expression<Func<T, DateTime?>> verifiedAt,
        Expression<Func<T, string?>> verifiedBy,
        Expression<Func<T, string?>> verificationSource)
    {
        map.Map(verifiedAt).Name("VerifiedAt").TypeConverterOption.Format(VerifiedAtFormat);
        map.Map(verifiedBy).Name("VerifiedBy");
        map.Map(verificationSource).Name("VerificationSource");
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
