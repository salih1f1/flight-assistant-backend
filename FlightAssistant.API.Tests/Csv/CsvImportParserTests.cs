using System.Text;
using FlightAssistant.API.Csv;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Domain.Common.Exceptions;

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
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt
            LHR,EGLL,Heathrow,London,GB,51.477,-0.461,83
            SVO,,Sheremetyevo,Moscow,RU,,,
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

    [Fact]
    public async Task ParseAirports_CsvStillHasTimezoneColumn_ThrowsClearDomainException()
    {
        // Airports CSVs exported before #285 still carry a Timezone column. Rather than silently
        // dropping it (which would leave an admin wondering why re-importing an old export had no
        // effect) or letting CsvHelper fail with an unmapped-member error, the parser rejects it
        // up front with a message naming the offending column.
        const string csv = """
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt,Timezone
            LHR,EGLL,Heathrow,London,GB,51.477,-0.461,83,Europe/London
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            CsvImportParser.ParseAsync<AirportImportDto, AirportImportDtoMap>(stream, rejectHeaders: ["Timezone"]));

        Assert.Contains("Timezone", ex.Message);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task ParseAirports_NoRejectHeadersConfigured_IgnoresStrayColumn()
    {
        // Without an explicit rejectHeaders list (the behaviour every other importer keeps), an
        // extra CSV column that isn't part of the DTO is silently ignored by CsvHelper's AutoMap.
        const string csv = """
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt,Timezone
            LHR,EGLL,Heathrow,London,GB,51.477,-0.461,83,Europe/London
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<AirportImportDto, AirportImportDtoMap>(stream);

        Assert.Single(result);
        Assert.Equal("LHR", result[0].Iata);
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

    [Fact]
    public async Task ParseLounges_MalformedProvidersJson_ThrowsCsvHelperException()
    {
        const string csv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers
            "Bad Lounge",LHR,T5,,24h,true,false,false,,not-valid-json
            """;
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream));
    }

    // ── #362: verification columns are optional, and VerifiedAt parses as UTC ──────

    [Fact]
    public async Task ParseAlliances_NoVerificationColumns_ParsesWithNullValues()
    {
        // The exact CSV shape an older export (pre-#362) would carry: no VerifiedAt/VerifiedBy/
        // VerificationSource columns at all. The three new columns must be optional, or this
        // (and every real old-format re-import) would throw a header validation error.
        const string csv = """
            Code,Name
            SA,Star Alliance
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(stream);

        Assert.Single(result);
        Assert.Null(result[0].VerifiedAt);
        Assert.Null(result[0].VerifiedBy);
        Assert.Null(result[0].VerificationSource);
    }

    [Fact]
    public async Task ParseAlliances_WithVerificationColumns_ParsesVerifiedAtAsUtc()
    {
        const string csv = """
            Code,Name,VerifiedAt,VerifiedBy,VerificationSource
            SA,Star Alliance,2026-09-14T20:31:12.1234567Z,admin@example.com,manual
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(stream);

        Assert.Single(result);
        Assert.NotNull(result[0].VerifiedAt);
        Assert.Equal(DateTimeKind.Utc, result[0].VerifiedAt!.Value.Kind);
        Assert.Equal("admin@example.com", result[0].VerifiedBy);
        Assert.Equal("manual", result[0].VerificationSource);
    }

    [Fact]
    public async Task ParseLounges_NoVerificationColumns_ParsesWithNullValues()
    {
        // Same optionality check as the flat-DTO case above, but for a ClassMap that maps
        // constructor Parameters explicitly rather than via AutoMap.
        const string csv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers
            "The Lounge",LHR,T5,,24h,true,false,false,,[]
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream);

        Assert.Single(result);
        Assert.Null(result[0].VerifiedAt);
        Assert.Null(result[0].VerifiedBy);
        Assert.Null(result[0].VerificationSource);
    }

    [Fact]
    public async Task ParseLounges_WithVerificationColumns_ParsesVerifiedAtAsUtc()
    {
        const string csv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers,VerifiedAt,VerifiedBy,VerificationSource
            "The Lounge",LHR,T5,,24h,true,false,false,,[],2026-09-14T20:31:12.1234567Z,admin@example.com,manual
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream);

        Assert.Single(result);
        Assert.NotNull(result[0].VerifiedAt);
        Assert.Equal(DateTimeKind.Utc, result[0].VerifiedAt!.Value.Kind);
        Assert.Equal("admin@example.com", result[0].VerifiedBy);
        Assert.Equal("manual", result[0].VerificationSource);
    }
}
