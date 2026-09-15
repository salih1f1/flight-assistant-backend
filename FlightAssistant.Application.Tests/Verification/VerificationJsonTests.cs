using FlightAssistant.Application.Verification;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>
/// The <c>differences</c>/<c>source_values</c> JSON shapes Task 4 will store verbatim in the
/// <c>jsonb</c> columns (#367 task 2, spec §2.3): camelCase property names and deterministic order.
/// </summary>
public class VerificationJsonTests
{
    [Fact]
    public void SerializeDifferences_UsesCamelCasePropertyNames()
    {
        var differences = new[] { new VerificationDifference("Name", "Heathrow", "Heathrow Airport", "ourairports") };

        var json = VerificationJson.SerializeDifferences(differences);

        Assert.Equal(
            """[{"column":"Name","ours":"Heathrow","source":"Heathrow Airport","sourceName":"ourairports"}]""",
            json);
    }

    [Fact]
    public void SerializeDifferences_NullOursOrSource_SerialisesAsJsonNull()
    {
        var differences = new[] { new VerificationDifference("Icao", null, "EGLL", "ourairports") };

        var json = VerificationJson.SerializeDifferences(differences);

        Assert.Equal(
            """[{"column":"Icao","ours":null,"source":"EGLL","sourceName":"ourairports"}]""",
            json);
    }

    [Fact]
    public void SerializeSourceValues_KeyedBySourceName_WithItsNormalisedRecord()
    {
        var sourceValues = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["ourairports"] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Iata"] = "LHR",
                ["Icao"] = "EGLL"
            }
        };

        var json = VerificationJson.SerializeSourceValues(sourceValues);

        Assert.Equal("""{"ourairports":{"Iata":"LHR","Icao":"EGLL"}}""", json);
    }

    [Fact]
    public void SerializeSourceValues_EmptyDictionary_SerialisesAsEmptyObject()
    {
        var sourceValues = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);

        Assert.Equal("{}", VerificationJson.SerializeSourceValues(sourceValues));
    }

    [Fact]
    public void DeserializeDifferences_ReadsBackWhatSerializeDifferencesWrote()
    {
        var differences = new[]
        {
            new VerificationDifference("Name", "İstanbul Airport", "Istanbul Airport", "ourairports"),
            new VerificationDifference("Icao", null, "LTFM", "other")
        };

        var restored = VerificationJson.DeserializeDifferences(VerificationJson.SerializeDifferences(differences));

        Assert.Equal(differences, restored);
    }

    [Fact]
    public void DeserializeSourceValues_ReadsBackWhatSerializeSourceValuesWrote_KeepingColumnKeysAsStored()
    {
        var sourceValues = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["ourairports"] = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Iata"] = "LHR", ["Icao"] = null },
            ["other"] = new Dictionary<string, string?>(StringComparer.Ordinal)
        };

        var restored = VerificationJson.DeserializeSourceValues(VerificationJson.SerializeSourceValues(sourceValues));

        Assert.Equal(new[] { "ourairports", "other" }, restored.Keys);
        Assert.Equal("LHR", restored["ourairports"]["Iata"]);
        Assert.True(restored["ourairports"].ContainsKey("Icao"));
        Assert.Null(restored["ourairports"]["Icao"]);
        Assert.False(restored["ourairports"].ContainsKey("iata"));
        Assert.Empty(restored["other"]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void DeserializeDifferences_NotAnArray_Throws(string json)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => VerificationJson.DeserializeDifferences(json));
    }

    /// <summary>The comparer's output round-trips directly into these helpers with no extra
    /// mapping step -- the integration Task 4 will actually use.</summary>
    [Fact]
    public void SerializeDifferences_FromComparerOutput_ProducesExpectedShape()
    {
        var ours = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Iata"] = "LHR" };
        var source = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Iata"] = "CDG" };
        var comparison = VerificationComparer.Compare(
            Domain.Verification.Enums.VerificationEntityType.Airport,
            FullAirportOurs(ours),
            new[] { new VerificationSourceResult("ourairports", VerificationSourceLookupResult.Found(FullAirportSource(source))) });

        var json = VerificationJson.SerializeDifferences(comparison.Differences);

        Assert.Contains("\"column\":\"Iata\"", json);
        Assert.Contains("\"sourceName\":\"ourairports\"", json);
    }

    private static Dictionary<string, string?> FullAirportOurs(Dictionary<string, string?> overrides) => Merge(overrides);
    private static Dictionary<string, string?> FullAirportSource(Dictionary<string, string?> overrides) => Merge(overrides);

    private static Dictionary<string, string?> Merge(Dictionary<string, string?> overrides)
    {
        var baseValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Iata"] = "LHR",
            ["Icao"] = "EGLL",
            ["Name"] = "Heathrow",
            ["City"] = "London",
            ["CountryCode"] = "GB",
            ["Latitude"] = "51.47",
            ["Longitude"] = "-0.4543",
            ["ElevationFt"] = "83"
        };
        foreach (var (key, value) in overrides) baseValues[key] = value;
        return baseValues;
    }
}
