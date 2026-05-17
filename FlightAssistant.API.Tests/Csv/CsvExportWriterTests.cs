using System.Text;
using FlightAssistant.API.Csv;
using FlightAssistant.Application.Queries.Admin.Export;

namespace FlightAssistant.API.Tests.Csv;

public class CsvExportWriterTests
{
    [Fact]
    public void WriteAlliances_ReturnsCsvWithHeaderAndRows()
    {
        var data = new List<AllianceExportDto>
        {
            new("SA", "Star Alliance"),
            new("OW", "Oneworld")
        };

        var bytes = CsvExportWriter.Write<AllianceExportDto, AllianceExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Code,Name", text);
        Assert.Contains("SA,Star Alliance", text);
        Assert.Contains("OW,Oneworld", text);
    }

    [Fact]
    public void WriteAirports_WithNullableFields_WritesEmptyForNull()
    {
        var data = new List<AirportExportDto>
        {
            new("SVO", null, "Sheremetyevo", "Moscow", "RU", null, null, null, null)
        };

        var bytes = CsvExportWriter.Write<AirportExportDto, AirportExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("SVO,", text);
        // null Icao should produce empty field, not the word "null"
        Assert.DoesNotContain("null", text);
    }

    [Fact]
    public void WriteLounges_WithProviders_EncodesProvidersAsJson()
    {
        var data = new List<LoungeExportDto>
        {
            new("The Lounge", "LHR", "T5", null, "24h", true, false, false, null,
                [new LoungeProviderLinkExportDto("AMEX", "Primary", null, null)])
        };

        var bytes = CsvExportWriter.Write<LoungeExportDto, LoungeExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Providers", text); // header
        Assert.Contains("AMEX", text);
        Assert.Contains("Primary", text);
    }

    [Fact]
    public void WriteLoyaltyPrograms_WithTiers_EncodesTiersAsJson()
    {
        var data = new List<LoyaltyProgramExportDto>
        {
            new("BAEC", "Executive Club", "AIRLINE", "BA", null,
                [new LoyaltyTierExportDto("GOLD", "Gold", 1, null, null)])
        };

        var bytes = CsvExportWriter.Write<LoyaltyProgramExportDto, LoyaltyProgramExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("BAEC", text);
        Assert.Contains("GOLD", text);
    }
}
