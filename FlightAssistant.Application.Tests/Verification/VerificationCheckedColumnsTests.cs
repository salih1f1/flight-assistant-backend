using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>
/// The checked-column registry (#367 task 2, spec §3): which columns are checked per entity type,
/// and that projecting our own entities produces exactly those columns -- never more, never fewer
/// -- so a column can't be checked on one side and forgotten on the other.
/// </summary>
public class VerificationCheckedColumnsTests
{
    [Fact]
    public void GetColumns_Airport_ListsExactlyTheSpecCheckedColumns()
    {
        var names = VerificationCheckedColumns.GetColumns(VerificationEntityType.Airport).Select(c => c.Name);

        Assert.Equal(
            new[] { "Iata", "Icao", "Name", "City", "CountryCode", "Latitude", "Longitude", "ElevationFt" },
            names);
    }

    [Fact]
    public void GetColumns_Airline_ListsExactlyTheSpecCheckedColumns_AllianceCodeExcluded()
    {
        var names = VerificationCheckedColumns.GetColumns(VerificationEntityType.Airline).Select(c => c.Name).ToList();

        Assert.Equal(new[] { "Iata", "Icao", "Name", "CountryCode" }, names);
        Assert.DoesNotContain("AllianceCode", names);
    }

    [Fact]
    public void GetColumns_Country_ListsExactlyTheSpecCheckedColumns_EuFlagsExcluded()
    {
        var names = VerificationCheckedColumns.GetColumns(VerificationEntityType.Country).Select(c => c.Name).ToList();

        Assert.Equal(new[] { "Code", "Name", "Alpha3", "NumericCode", "Region", "Subregion" }, names);
        Assert.DoesNotContain("IsEU", names);
        Assert.DoesNotContain("IsSchengen", names);
        Assert.DoesNotContain("IsEEA", names);
    }

    [Fact]
    public void Project_Airport_ProducesExactlyTheCheckedColumnKeys_WithFormattedValues()
    {
        var airport = Airport.Create("lhr", "egll", "Heathrow", "London", "gb", 51.4700, -0.4543, 83);

        var values = VerificationCheckedColumns.Project(airport);

        Assert.Equal(
            VerificationCheckedColumns.GetColumns(VerificationEntityType.Airport).Select(c => c.Name).ToHashSet(),
            values.Keys.ToHashSet());

        Assert.Equal("LHR", values["Iata"]);
        Assert.Equal("EGLL", values["Icao"]);
        Assert.Equal("Heathrow", values["Name"]);
        Assert.Equal("London", values["City"]);
        Assert.Equal("GB", values["CountryCode"]);
        Assert.Equal("51.47", values["Latitude"]);
        Assert.Equal("-0.4543", values["Longitude"]);
        Assert.Equal("83", values["ElevationFt"]);
    }

    [Fact]
    public void Project_Airport_NullOptionalNumbers_ProjectToNull()
    {
        var airport = Airport.Create("lhr", null, "Heathrow", "London", "gb");

        var values = VerificationCheckedColumns.Project(airport);

        Assert.Null(values["Icao"]);
        Assert.Null(values["Latitude"]);
        Assert.Null(values["Longitude"]);
        Assert.Null(values["ElevationFt"]);
    }

    [Fact]
    public void Project_Airline_ProducesExactlyTheCheckedColumnKeys_AllianceCodeNotProjected()
    {
        var airline = Airline.Create("BA", "BAW", "British Airways", "gb", "OW");

        var values = VerificationCheckedColumns.Project(airline);

        Assert.Equal(
            VerificationCheckedColumns.GetColumns(VerificationEntityType.Airline).Select(c => c.Name).ToHashSet(),
            values.Keys.ToHashSet());
        Assert.Equal("BA", values["Iata"]);
        Assert.Equal("BAW", values["Icao"]);
        Assert.Equal("British Airways", values["Name"]);
        Assert.Equal("GB", values["CountryCode"]);
    }

    [Fact]
    public void Project_Country_ProducesExactlyTheCheckedColumnKeys_EuFlagsNotProjected()
    {
        var country = Country.Create("GB", "United Kingdom", "GBR", 826, "Europe", "Northern Europe", isEU: false, isSchengen: false, isEEA: false);

        var values = VerificationCheckedColumns.Project(country);

        Assert.Equal(
            VerificationCheckedColumns.GetColumns(VerificationEntityType.Country).Select(c => c.Name).ToHashSet(),
            values.Keys.ToHashSet());
        Assert.Equal("GB", values["Code"]);
        Assert.Equal("United Kingdom", values["Name"]);
        Assert.Equal("GBR", values["Alpha3"]);
        Assert.Equal("826", values["NumericCode"]);
        Assert.Equal("Europe", values["Region"]);
        Assert.Equal("Northern Europe", values["Subregion"]);
    }
}
