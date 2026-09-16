using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>
/// The comparer's matching rules, precedence and output shape (#367 task 2, spec §3.1, §4.2).
/// Every test builds "ours" and a source's record directly as dictionaries, isolated from EF and
/// from <see cref="VerificationCheckedColumns"/>'s entity projection (covered separately in
/// <c>VerificationCheckedColumnsTests</c>).
/// </summary>
public class VerificationComparerTests
{
    private const string OurAirports = "ourairports";
    private const string Mledoze = "mledoze";

    private static Dictionary<string, string?> AirportValues(
        string? iata = "LHR", string? icao = "EGLL", string? name = "Heathrow", string? city = "London",
        string? countryCode = "GB", string? latitude = "51.47", string? longitude = "-0.4543", string? elevationFt = "83") =>
        new(StringComparer.Ordinal)
        {
            ["Iata"] = iata,
            ["Icao"] = icao,
            ["Name"] = name,
            ["City"] = city,
            ["CountryCode"] = countryCode,
            ["Latitude"] = latitude,
            ["Longitude"] = longitude,
            ["ElevationFt"] = elevationFt
        };

    private static Dictionary<string, string?> CountryValues(
        string? code = "GB", string? name = "United Kingdom", string? alpha3 = "GBR",
        string? numericCode = "826", string? region = "Europe", string? subregion = "Northern Europe") =>
        new(StringComparer.Ordinal)
        {
            ["Code"] = code,
            ["Name"] = name,
            ["Alpha3"] = alpha3,
            ["NumericCode"] = numericCode,
            ["Region"] = region,
            ["Subregion"] = subregion
        };

    private static VerificationSourceResult Found(string sourceName, IReadOnlyDictionary<string, string?> values) =>
        new(sourceName, VerificationSourceLookupResult.Found(values));

    private static VerificationSourceResult NotFound(string sourceName) =>
        new(sourceName, VerificationSourceLookupResult.NotFound);

    /// <summary>Removes <paramref name="key"/> entirely, as opposed to mapping it to
    /// <c>null</c> -- the two are not written to be the same code path, so tests need to be able
    /// to construct a record that's missing a key outright.</summary>
    private static Dictionary<string, string?> WithoutKey(Dictionary<string, string?> values, string key)
    {
        values.Remove(key);
        return values;
    }

    // ---- Codes ---------------------------------------------------------------------------

    [Fact]
    public void Compare_CodeColumn_MatchesAfterTrimAndUpperCase()
    {
        var ours = AirportValues(countryCode: "gb");
        var source = AirportValues(countryCode: " GB ");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Compare_CodeColumn_Mismatch()
    {
        var ours = AirportValues(iata: "LHR");
        var source = AirportValues(iata: "CDG");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        var difference = Assert.Single(result.Differences);
        Assert.Equal("Iata", difference.Column);
        Assert.Equal("LHR", difference.Ours);
        Assert.Equal("CDG", difference.Source);
        Assert.Equal(OurAirports, difference.SourceName);
    }

    // ---- NumericCode -----------------------------------------------------------------------

    [Fact]
    public void Compare_NumericCode_LeadingZerosEqualsBareNumber()
    {
        var ours = CountryValues(numericCode: "4");
        var source = CountryValues(numericCode: "004");

        var result = VerificationComparer.Compare(VerificationEntityType.Country, ours, new[] { Found(Mledoze, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Compare_NumericCode_DifferentNumbers_Mismatch()
    {
        var ours = CountryValues(numericCode: "4");
        var source = CountryValues(numericCode: "8");

        var result = VerificationComparer.Compare(VerificationEntityType.Country, ours, new[] { Found(Mledoze, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
    }

    // ---- Text (accent/case/punctuation normalisation, end to end through Compare) ----------

    [Fact]
    public void Compare_TextColumn_AccentAndPunctuationInsensitive_Matches()
    {
        var ours = CountryValues(name: "Côte d'Ivoire");
        var source = CountryValues(name: "Cote d Ivoire");

        var result = VerificationComparer.Compare(VerificationEntityType.Country, ours, new[] { Found(Mledoze, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Compare_TextColumn_GenuinelyDifferentNames_Mismatch()
    {
        var ours = AirportValues(name: "Heathrow");
        var source = AirportValues(name: "Gatwick");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal("Name", Assert.Single(result.Differences).Column);
    }

    // ---- Tolerances: exact boundary is a match, just past it is not ------------------------

    [Theory]
    [InlineData("Latitude", "0", "0.01")] // |0 - 0.01| == 0.01 exactly, no subtraction rounding either side
    [InlineData("Longitude", "0", "0.01")]
    public void Compare_Coordinate_ExactlyAtTolerance_Matches(string column, string oursValue, string sourceValue)
    {
        var ours = AirportValues(latitude: column == "Latitude" ? oursValue : "51.47", longitude: column == "Longitude" ? oursValue : "-0.4543");
        var source = AirportValues(latitude: column == "Latitude" ? sourceValue : "51.47", longitude: column == "Longitude" ? sourceValue : "-0.4543");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Theory]
    [InlineData("Latitude", "0", "0.0101")]
    [InlineData("Longitude", "0", "0.0101")]
    public void Compare_Coordinate_JustPastTolerance_Mismatches(string column, string oursValue, string sourceValue)
    {
        var ours = AirportValues(latitude: column == "Latitude" ? oursValue : "51.47", longitude: column == "Longitude" ? oursValue : "-0.4543");
        var source = AirportValues(latitude: column == "Latitude" ? sourceValue : "51.47", longitude: column == "Longitude" ? sourceValue : "-0.4543");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal(column, Assert.Single(result.Differences).Column);
    }

    [Fact]
    public void Compare_Elevation_ExactlyAtTolerance_Matches()
    {
        var ours = AirportValues(elevationFt: "0");
        var source = AirportValues(elevationFt: "50");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Compare_Elevation_JustPastTolerance_Mismatches()
    {
        var ours = AirportValues(elevationFt: "0");
        var source = AirportValues(elevationFt: "51");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal("ElevationFt", Assert.Single(result.Differences).Column);
    }

    // ---- Empty values ------------------------------------------------------------------------

    [Fact]
    public void Compare_BothSidesEmpty_Matches()
    {
        var ours = AirportValues(icao: null);
        var source = AirportValues(icao: "   ");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Compare_OneSideEmpty_Mismatches()
    {
        var ours = AirportValues(icao: null);
        var source = AirportValues(icao: "EGLL");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal("Icao", Assert.Single(result.Differences).Column);
    }

    // ---- Unchecked columns ---------------------------------------------------------------------

    [Fact]
    public void Compare_UnknownColumnKey_IsIgnored_Airline()
    {
        var ours = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Iata"] = "BA", ["Icao"] = "BAW", ["Name"] = "British Airways",
            ["CountryCode"] = "GB", // not a checked Airline column (R1, #401): aeroapi reports a name, never a code
            ["AllianceCode"] = "OW" // not a checked Airline column
        };
        var source = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Iata"] = "BA", ["Icao"] = "BAW", ["Name"] = "British Airways",
            ["CountryCode"] = "FR", // deliberately differs -- must not affect the outcome
            ["AllianceCode"] = "ST" // deliberately differs -- must not affect the outcome
        };

        var result = VerificationComparer.Compare(VerificationEntityType.Airline, ours, new[] { Found("aeroapi", source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Compare_UnknownColumnKey_IsIgnored_CountryIsEUFlag()
    {
        var ours = new Dictionary<string, string?>(CountryValues())
        {
            ["IsEU"] = "true"
        };
        var source = new Dictionary<string, string?>(CountryValues())
        {
            ["IsEU"] = "false" // deliberately differs -- must not affect the outcome
        };

        var result = VerificationComparer.Compare(VerificationEntityType.Country, ours, new[] { Found(Mledoze, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    // ---- Several sources: precedence and per-source naming --------------------------------

    [Fact]
    public void Compare_SeveralSources_OneDisagrees_MismatchNamingOnlyThatSource()
    {
        var ours = CountryValues();
        var agreeingSource = CountryValues();
        var disagreeingSource = CountryValues(region: "Not Europe");

        var result = VerificationComparer.Compare(
            VerificationEntityType.Country,
            ours,
            new[] { Found(Mledoze, agreeingSource), Found("aeroapi", disagreeingSource) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        var difference = Assert.Single(result.Differences);
        Assert.Equal("Region", difference.Column);
        Assert.Equal("aeroapi", difference.SourceName);
        Assert.DoesNotContain(Mledoze, result.Summary);
        Assert.Contains("aeroapi", result.Summary);
    }

    [Fact]
    public void Compare_SeveralSources_OneNotFoundAndAnotherMismatching_OutcomeIsNotFound()
    {
        var ours = CountryValues();
        var mismatchingSource = CountryValues(region: "Not Europe");

        var result = VerificationComparer.Compare(
            VerificationEntityType.Country,
            ours,
            new[] { NotFound(Mledoze), Found("aeroapi", mismatchingSource) });

        Assert.Equal(VerificationComparisonOutcome.NotFound, result.Outcome);
        Assert.Empty(result.Differences);
        // The source that answered is still reported; the one that didn't contributes no entry.
        Assert.True(result.SourceValues.ContainsKey("aeroapi"));
        Assert.False(result.SourceValues.ContainsKey(Mledoze));
        Assert.Contains(Mledoze, result.Summary);
    }

    [Fact]
    public void Compare_AllSourcesFoundAndMatch_Verified()
    {
        var ours = AirportValues();
        var result = VerificationComparer.Compare(
            VerificationEntityType.Airport, ours, new[] { Found(OurAirports, AirportValues()) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
        Assert.Empty(result.Differences);
        Assert.Null(result.Summary);
        Assert.True(result.SourceValues.ContainsKey(OurAirports));
    }

    // ---- Output shape ------------------------------------------------------------------------

    [Fact]
    public void Compare_OutputShape_DifferencesCarryColumnOursSourceAndSourceName()
    {
        var ours = AirportValues(name: "Heathrow", city: "London");
        var source = AirportValues(name: "Heathrow Airport", city: "Greater London");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal(2, result.Differences.Count);
        Assert.All(result.Differences, d => Assert.Equal(OurAirports, d.SourceName));
        Assert.Contains(result.Differences, d => d.Column == "Name" && d.Ours == "Heathrow" && d.Source == "Heathrow Airport");
        Assert.Contains(result.Differences, d => d.Column == "City" && d.Ours == "London" && d.Source == "Greater London");
    }

    [Fact]
    public void Compare_OutputShape_SummaryListsDifferingColumnsAndSourceName()
    {
        var ours = AirportValues(name: "Heathrow", city: "London");
        var source = AirportValues(name: "Heathrow Airport", city: "Greater London");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal($"Name, City differ ({OurAirports})", result.Summary);
    }

    // ---- Fix round 1 (#367 task 2 review): empty-sources contract ---------------------------

    [Fact]
    public void Compare_EmptySources_Throws()
    {
        var ours = AirportValues();

        var exception = Assert.Throws<ArgumentException>(() =>
            VerificationComparer.Compare(VerificationEntityType.Airport, ours, Array.Empty<VerificationSourceResult>()));

        Assert.Equal("sources", exception.ParamName);
    }

    // ---- Fix round 1: malformed source values never throw, always mismatch ------------------

    [Fact]
    public void Compare_NumericCode_MalformedSourceValue_MismatchesWithoutThrowing()
    {
        var ours = CountryValues(numericCode: "4");
        var source = CountryValues(numericCode: "abc");

        var result = VerificationComparer.Compare(VerificationEntityType.Country, ours, new[] { Found(Mledoze, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal("NumericCode", Assert.Single(result.Differences).Column);
    }

    [Fact]
    public void Compare_Coordinate_MalformedSourceValue_MismatchesWithoutThrowing()
    {
        var ours = AirportValues(latitude: "51.47");
        var source = AirportValues(latitude: "north");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Contains(result.Differences, d => d.Column == "Latitude");
    }

    [Fact]
    public void Compare_Elevation_MalformedSourceValue_MismatchesWithoutThrowing()
    {
        var ours = AirportValues(elevationFt: "83");
        var source = AirportValues(elevationFt: "12.5ft");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Contains(result.Differences, d => d.Column == "ElevationFt");
    }

    // ---- Fix round 1: a checked column missing entirely, vs mapped to null ------------------

    [Fact]
    public void Compare_SourceOmitsCheckedColumnKeyEntirely_MismatchesAgainstOurNonEmptyValue()
    {
        var ours = AirportValues(city: "London");
        var source = WithoutKey(AirportValues(), "City");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal("City", Assert.Single(result.Differences).Column);
    }

    [Fact]
    public void Compare_BothSidesOmitCheckedColumnKeyEntirely_Matches()
    {
        var ours = WithoutKey(AirportValues(), "Icao");
        var source = WithoutKey(AirportValues(), "Icao");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Compare_OurValuesOmitCheckedColumnKeyEntirely_MismatchesAgainstSourceNonEmptyValue()
    {
        var ours = WithoutKey(AirportValues(), "Iata");
        var source = AirportValues(iata: "LHR");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal("Iata", Assert.Single(result.Differences).Column);
    }

    // ---- Fix round 1 (minor, folded in): punctuation-only and digit-bearing text ------------

    /// <summary>Two different punctuation-only values ("-" and an em dash) both normalise to the
    /// empty string, so they compare equal through the ordinary text-equality path -- not through
    /// the raw <c>IsEmpty</c> pre-check, since neither raw value is null/blank/whitespace. See the
    /// report for why this can't be broken as a behaviour distinct from text equality itself.</summary>
    [Fact]
    public void Compare_TextColumn_PunctuationOnlyValues_BothNormaliseToEmpty_Matches()
    {
        var ours = AirportValues(name: "-");
        var source = AirportValues(name: "—");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }

    [Fact]
    public void Compare_TextColumn_PunctuationOnlyValue_MismatchesAgainstRealText()
    {
        var ours = AirportValues(name: "-");
        var source = AirportValues(name: "Paris");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal("Name", Assert.Single(result.Differences).Column);
    }

    [Fact]
    public void Compare_TextColumn_DigitsPreserved_DifferentDigits_Mismatch()
    {
        var ours = AirportValues(name: "Terminal 2");
        var source = AirportValues(name: "Terminal 3");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Mismatch, result.Outcome);
        Assert.Equal("Name", Assert.Single(result.Differences).Column);
    }

    [Fact]
    public void Compare_TextColumn_DigitsPreserved_PunctuationVariant_Matches()
    {
        var ours = AirportValues(name: "Terminal-2");
        var source = AirportValues(name: "terminal 2");

        var result = VerificationComparer.Compare(VerificationEntityType.Airport, ours, new[] { Found(OurAirports, source) });

        Assert.Equal(VerificationComparisonOutcome.Verified, result.Outcome);
    }
}
