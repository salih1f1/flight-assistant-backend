using System.Text;
using FlightAssistant.API.Csv;
using FlightAssistant.Application.Commands.Admin.Import;

namespace FlightAssistant.API.Tests.Csv;

public class CsvImportParserTests
{
    // ── Flat entity ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAlliances_ValidCsv_ReturnsDtos()
    {
        const string csv = """
            Code,Name
            SA,Star Alliance
            OW,Oneworld
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(stream);

        Assert.Equal(2, result.Count);
        Assert.Equal("SA", result[0].Code);
        Assert.Equal("Star Alliance", result[0].Name);
        Assert.Equal("OW", result[1].Code);
    }

    [Fact]
    public async Task ParseAirports_WithNullableFields_ReturnsDtos()
    {
        const string csv = """
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt,Timezone
            LHR,EGLL,Heathrow,London,GB,51.477,-0.461,83,Europe/London
            SVO,,Sheremetyevo,Moscow,RU,,,,,
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<AirportImportDto, AirportImportDtoMap>(stream);

        Assert.Equal(2, result.Count);
        Assert.Equal("LHR", result[0].Iata);
        Assert.Equal(51.477, result[0].Latitude);
        Assert.Equal(83, result[0].ElevationFt);
        Assert.Equal("SVO", result[1].Iata);
        Assert.Null(result[1].Icao);
        Assert.Null(result[1].Latitude);
        Assert.Null(result[1].ElevationFt);
    }

    // ── Nested entity (JSON column) ────────────────────────────────────────────

    [Fact]
    public async Task ParseLounges_WithProvidersJsonColumn_ReturnsDtos()
    {
        const string csv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers
            "The Lounge",LHR,T5,,24h,true,false,false,,"[{""providerCode"":""AMEX"",""role"":""Primary"",""minAllianceTierCode"":null,""notes"":null}]"
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream);

        Assert.Single(result);
        Assert.Equal("The Lounge", result[0].Name);
        Assert.Single(result[0].Providers);
        Assert.Equal("AMEX", result[0].Providers[0].ProviderCode);
        Assert.Equal("Primary", result[0].Providers[0].Role);
    }

    [Fact]
    public async Task ParseLounges_EmptyProviders_ReturnsEmptyList()
    {
        const string csv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers
            "Empty Lounge",JFK,T4,,,,false,false,,[]
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream);

        Assert.Single(result);
        Assert.Empty(result[0].Providers);
    }
}
