using System.Globalization;
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
            new("SA", "Star Alliance", null, null, null),
            new("OW", "Oneworld", null, null, null)
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
            new("SVO", null, "Sheremetyevo", "Moscow", "RU", null, null, null, null, null, null)
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
                [new LoungeProviderLinkExportDto("AMEX", "Primary", null, null)], null, null, null)
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
                [new LoyaltyTierExportDto("GOLD", "Gold", 1, null, null)], null, null, null)
        };

        var bytes = CsvExportWriter.Write<LoyaltyProgramExportDto, LoyaltyProgramExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("BAEC", text);
        Assert.Contains("GOLD", text);
    }

    // ── #362: verification columns ───────────────────────────────────────────

    [Fact]
    public void WriteAlliances_VerificationColumns_ArePresentInOrderAfterExistingColumns()
    {
        var verifiedAt = new DateTime(2026, 9, 14, 20, 31, 12, 123, DateTimeKind.Utc).AddTicks(4560);
        var data = new List<AllianceExportDto>
        {
            new("SA", "Star Alliance", verifiedAt, "admin@example.com", "manual")
        };

        var bytes = CsvExportWriter.Write<AllianceExportDto, AllianceExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);
        var header = text.Split('\n')[0].TrimEnd('\r').TrimStart('﻿');

        Assert.Equal("Code,Name,VerifiedAt,VerifiedBy,VerificationSource", header);
        Assert.Contains("admin@example.com,manual", text);
    }

    [Fact]
    public void WriteAlliances_VerifiedAt_IsWrittenInRoundTripUtcFormat()
    {
        var verifiedAt = new DateTime(2026, 9, 14, 20, 31, 12, 123, DateTimeKind.Utc).AddTicks(4560);
        var data = new List<AllianceExportDto> { new("SA", "Star Alliance", verifiedAt, "admin", "manual") };

        var bytes = CsvExportWriter.Write<AllianceExportDto, AllianceExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);
        var dataLine = text.Split('\n')[1].TrimEnd('\r');
        var verifiedAtField = dataLine.Split(',')[2];

        // The "O" round-trip format: e.g. 2026-09-14T20:31:12.1234560Z — parsing it back with
        // round-trip styles must reproduce the exact same UTC instant.
        var parsedBack = DateTime.ParseExact(verifiedAtField, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, parsedBack.Kind);
        Assert.Equal(verifiedAt, parsedBack);
    }

    [Fact]
    public void WriteAirports_NullVerifiedAt_WritesEmptyField()
    {
        var data = new List<AirportExportDto>
        {
            new("LHR", "EGLL", "Heathrow", "London", "GB", null, null, null, null, null, null)
        };

        var bytes = CsvExportWriter.Write<AirportExportDto, AirportExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("null", text);
    }
}
