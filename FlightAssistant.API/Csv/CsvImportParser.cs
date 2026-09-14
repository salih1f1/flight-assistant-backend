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
    /// Registers the three optional verification columns (#362) — <c>VerifiedAt</c>/
    /// <c>VerifiedBy</c>/<c>VerificationSource</c> — as fresh, explicit
    /// <see cref="ClassMap.Parameter(string)"/> entries, each <c>Optional()</c>;
    /// <c>VerifiedAt</c> additionally with UTC <see cref="DateTimeStyles"/>. Every import DTO is a
    /// positional record with no parameterless constructor, so CsvHelper always builds it via the
    /// constructor.
    ///
    /// <para>
    /// <b>#362 fix round 1: why this isn't AutoMap plus an override.</b> Two approaches were tried
    /// and rejected before this one:
    /// </para>
    /// <list type="number">
    /// <item><c>AutoMap(...)</c> then <see cref="ClassMap{T}.Map"/> to override the three members —
    /// fails, because <c>Map()</c> configures a *property*, which CsvHelper never reads when
    /// constructing a constructor-only type. AutoMap's own non-optional
    /// <see cref="ClassMap.Parameter(string)"/> for the same name stays in force underneath it, so
    /// an older CSV without the columns still failed header validation.</item>
    /// <item><c>AutoMap(...)</c> then find AutoMap's own auto-registered <c>ParameterMap</c> in
    /// <see cref="ClassMap.ParameterMaps"/> by name and configure it <em>in place</em>
    /// (<c>Optional()</c>, UTC styles, even an explicit <c>.Name(...)</c>) — looks like it should
    /// work (same object, <c>Data.IsOptional</c> confirmed <c>true</c> right up to the parse call),
    /// but still throws the same header-validation error. Root cause, found via a minimal repro
    /// isolated from this codebase: CsvHelper 33.1.0's <c>AutoMap</c> leaves every auto-registered
    /// constructor parameter's <c>Data.Names</c> empty (confirmed by inspection, including for
    /// parameters that resolve to a header without any override, like <c>Code</c>/<c>Name</c>), and
    /// <c>CsvReader.ValidateHeader(ClassMap, List&lt;InvalidHeader&gt;)</c> resolves a header via
    /// <c>GetFieldIndex(Data.Names, ...)</c> — which cannot resolve anything from an empty
    /// collection, <c>Data.IsOptional</c> notwithstanding. This is a CsvHelper limitation for
    /// constructor-parameter mapping, not a mistake in how it was invoked here.</item>
    /// </list>
    /// <para>
    /// The explicit registration below — proved correct by the same repro — replaces AutoMap
    /// entirely for the flat import maps rather than trying to patch its output; every constructor
    /// parameter, verification columns included, is mapped fresh.
    /// <c>ImportClassMapCompletenessTests</c> (API.Tests) guards all 8 maps against silently
    /// dropping a future constructor parameter now that AutoMap no longer does that for free.
    /// </para>
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
