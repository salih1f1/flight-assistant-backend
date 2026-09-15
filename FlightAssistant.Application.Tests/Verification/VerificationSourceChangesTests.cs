using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>
/// <see cref="VerificationSourceChanges"/> against <see cref="VerificationCheckedColumns"/>: every
/// checked column accept-source can write lands on its own property, so a column added to the
/// registry, or two <c>Update</c> arguments swapped, fails here rather than in production.
/// </summary>
public class VerificationSourceChangesTests
{
    public static TheoryData<VerificationEntityType, string> WritableColumns()
    {
        var data = new TheoryData<VerificationEntityType, string>();
        foreach (var entityType in Enum.GetValues<VerificationEntityType>())
        foreach (var column in VerificationCheckedColumns.GetColumns(entityType))
        {
            // The country's key; see ApplyTo_CountryCode_IsRefused.
            if (entityType == VerificationEntityType.Country && column.Name == nameof(Country.Code)) continue;
            data.Add(entityType, column.Name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WritableColumns))]
    public void ApplyTo_OneCheckedColumn_ChangesThatColumnAndNoOther(VerificationEntityType entityType, string columnName)
    {
        var column = VerificationCheckedColumns.GetColumns(entityType).Single(c => c.Name == columnName);
        var raw = NewValue(column.Kind);
        var changes = VerificationSourceChanges.Parse(entityType, new Dictionary<string, string?> { [columnName] = raw });

        var (before, after) = ApplyToAFreshEntity(entityType, changes);

        Assert.True(VerificationComparer.ColumnsMatch(column.Kind, after[columnName], raw),
            $"{entityType}.{columnName} is '{after[columnName]}' after accepting '{raw}'.");
        foreach (var other in before.Keys.Where(k => k != columnName))
            Assert.Equal(before[other], after[other]);
    }

    [Fact]
    public void ApplyTo_CountryCode_IsRefused()
    {
        var changes = VerificationSourceChanges.Parse(
            VerificationEntityType.Country, new Dictionary<string, string?> { [nameof(Country.Code)] = "UK" });
        var country = Country.Create("GB", "United Kingdom");

        var ex = Assert.Throws<DomainException>(() => changes.ApplyTo(country));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("GB", country.Code);
    }

    [Fact]
    public void ApplyTo_AnotherEntityType_Throws()
    {
        var changes = VerificationSourceChanges.Parse(
            VerificationEntityType.Airport, new Dictionary<string, string?> { [nameof(Airport.Name)] = "Name" });

        Assert.Throws<InvalidOperationException>(() => changes.ApplyTo(Country.Create("GB", "United Kingdom")));
    }

    [Fact]
    public void Parse_AColumnThatIsNotChecked_Throws()
    {
        Assert.Throws<ArgumentException>(() => VerificationSourceChanges.Parse(
            VerificationEntityType.Airline, new Dictionary<string, string?> { [nameof(Airline.AllianceCode)] = "OW" }));
    }

    private static string NewValue(VerificationColumnKind kind) => kind switch
    {
        VerificationColumnKind.Code => "ZZ",
        VerificationColumnKind.Text => "Changed",
        VerificationColumnKind.Coordinate => "12.5",
        VerificationColumnKind.Elevation => "1234",
        VerificationColumnKind.NumericCode => "999",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static (IReadOnlyDictionary<string, string?> Before, IReadOnlyDictionary<string, string?> After) ApplyToAFreshEntity(
        VerificationEntityType entityType, VerificationSourceChanges changes)
    {
        switch (entityType)
        {
            case VerificationEntityType.Airport:
                var airport = Airport.Create("LHR", "EGLL", "London Heathrow", "London", "GB", 51.4706, -0.461941, 83);
                var airportBefore = VerificationCheckedColumns.Project(airport);
                changes.ApplyTo(airport);
                return (airportBefore, VerificationCheckedColumns.Project(airport));

            case VerificationEntityType.Airline:
                var airline = Airline.Create("BA", "BAW", "British Airways", "GB", "OW");
                var airlineBefore = VerificationCheckedColumns.Project(airline);
                changes.ApplyTo(airline);
                Assert.Equal("OW", airline.AllianceCode);
                return (airlineBefore, VerificationCheckedColumns.Project(airline));

            case VerificationEntityType.Country:
                var country = Country.Create("GB", "United Kingdom", "GBR", 826, "Europe", "Northern Europe", true, false, true);
                var countryBefore = VerificationCheckedColumns.Project(country);
                changes.ApplyTo(country);
                Assert.Equal<(bool?, bool?, bool?)>((true, false, true), (country.IsEU, country.IsSchengen, country.IsEEA));
                return (countryBefore, VerificationCheckedColumns.Project(country));

            default:
                throw new ArgumentOutOfRangeException(nameof(entityType));
        }
    }
}
