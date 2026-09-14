using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class AdminImportEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private const string ValidAlliancesCsv = """
        Code,Name
        SA,Star Alliance
        OW,Oneworld
        """;

    private static MultipartFormDataContent BuildMultipart(string csvContent, string contentType = "text/csv", string fileName = "import.csv")
    {
        var bytes = Encoding.UTF8.GetBytes(csvContent);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", fileName);
        return form;
    }

    /// <summary>
    /// Finds the one CSV data row starting with <paramref name="rowPrefix"/> and returns its
    /// comma-split fields — used instead of a whole-export <c>Contains</c> check, which (over the
    /// shared InMemory database other tests in this collection also populate) can't tell "this
    /// row's own trailing fields are exactly right" apart from "some other row anywhere in the
    /// export happens to contain this substring".
    /// </summary>
    private static string[] FindDataRowFields(string csv, string rowPrefix)
    {
        var line = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Single(l => l.StartsWith(rowPrefix, StringComparison.Ordinal));
        return line.Split(',');
    }

    [Fact]
    public async Task ImportAlliances_ValidCsv_Returns200()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PostAsync("admin/import/alliances", BuildMultipart(ValidAlliancesCsv));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ImportLounges_MixOfValidAndInvalidRows_Returns207()
    {
        var client = factory.CreateAdminClient();

        const string airportsCsv = """
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt
            LHR,EGLL,Heathrow Airport,London,GB,51.4706,-0.461941,83
            """;
        var airportsResponse = await client.PostAsync("admin/import/airports", BuildMultipart(airportsCsv));
        Assert.Equal(HttpStatusCode.OK, airportsResponse.StatusCode);

        const string loungesCsv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers
            "Galleries Club",LHR,T5,,24h,true,false,false,,
            "Unknown Airport Lounge",ZZZ,,,,true,false,false,,
            """;

        var response = await client.PostAsync("admin/import/lounges", BuildMultipart(loungesCsv));

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
    }

    [Fact]
    public async Task ImportAirports_CsvHasStaleTimezoneColumn_Returns400WithClearMessage()
    {
        var client = factory.CreateAdminClient();

        const string airportsCsv = """
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt,Timezone
            LHR,EGLL,Heathrow Airport,London,GB,51.4706,-0.461941,83,Europe/London
            """;

        var response = await client.PostAsync("admin/import/airports", BuildMultipart(airportsCsv));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Timezone", body);
    }

    [Fact]
    public async Task ImportAlliances_WrongContentType_Returns400()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PostAsync(
            "admin/import/alliances",
            BuildMultipart(ValidAlliancesCsv, contentType: "application/octet-stream"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("alliances")]
    [InlineData("countries")]
    [InlineData("airports")]
    [InlineData("airlines")]
    [InlineData("lounge-providers")]
    [InlineData("loyalty-program-kinds")]
    [InlineData("lounges")]
    [InlineData("loyalty-programs")]
    public async Task Import_WrongContentType_Returns400(string route)
    {
        var client = factory.CreateAdminClient();

        var response = await client.PostAsync(
            $"admin/import/{route}",
            BuildMultipart(ValidAlliancesCsv, contentType: "application/octet-stream"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportAlliances_FileTooLarge_Returns400()
    {
        var client = factory.CreateAdminClient();

        // Build a CSV well over the 10 MB limit: header + many padded rows.
        var sb = new StringBuilder();
        sb.AppendLine("Code,Name");
        var padding = new string('A', 100);
        for (var i = 0; i < 120_000; i++)
            sb.AppendLine($"A{i:D6},{padding}");

        var response = await client.PostAsync(
            "admin/import/alliances",
            BuildMultipart(sb.ToString()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportAlliances_MissingRequiredHeader_Returns400()
    {
        var client = factory.CreateAdminClient();

        // "Name" header is missing entirely, which CsvHelper rejects when
        // strict header validation applies to the AllianceImportDto map.
        const string csv = """
            Code
            SA
            """;

        var response = await client.PostAsync("admin/import/alliances", BuildMultipart(csv));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportLounges_MalformedProvidersJson_Returns400()
    {
        var client = factory.CreateAdminClient();

        const string csv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers
            "Bad Lounge",LHR,T5,,24h,true,false,false,,not-valid-json
            """;

        var response = await client.PostAsync("admin/import/lounges", BuildMultipart(csv));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── #362: import verifies rows, export carries verification details ───────────

    [Fact]
    public async Task ImportAlliances_OldFormatCsv_RowEndsVerifiedAsImportByActor()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateAdminClient(userId);

        var importResponse = await client.PostAsync("admin/import/alliances", BuildMultipart(ValidAlliancesCsv));
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);

        var exportResponse = await client.GetAsync("admin/export/alliances");
        var exportCsv = await exportResponse.Content.ReadAsStringAsync();

        Assert.Contains("SA,Star Alliance,", exportCsv);
        // VerifiedBy,VerificationSource are adjacent trailing columns: this confirms the row was
        // stamped by this request's actor with source `import`, not left unverified or manual.
        Assert.Contains($"{userId},import", exportCsv);
    }

    [Fact]
    public async Task ImportAlliances_OnePartiallyFilledVerificationRow_Returns207AndImportsTheOthers()
    {
        var client = factory.CreateAdminClient();

        // Codes distinct from every other test in this class — the InMemory database is shared
        // across the collection's tests, so reusing "SA"/"OW" would make this an update rather
        // than an insert depending on run order. "ZP" fills only VerifiedBy — partial verification
        // data, rejected into Errors. "ZQ" fills none — stamped `import` and still succeeds in the
        // same file.
        const string csv = """
            Code,Name,VerifiedAt,VerifiedBy,VerificationSource
            ZP,Zeta Partial Alliance,,prior-admin,
            ZQ,Zeta Quiet Alliance,,,
            """;

        var response = await client.PostAsync("admin/import/alliances", BuildMultipart(csv));

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("inserted").GetInt32());
        var errors = doc.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Single(errors);
        Assert.Contains("ZP", errors[0]);

        var exportResponse = await client.GetAsync("admin/export/alliances");
        var exportCsv = await exportResponse.Content.ReadAsStringAsync();
        Assert.Contains("ZQ,Zeta Quiet Alliance,", exportCsv);
        Assert.DoesNotContain("ZP,Zeta Partial Alliance", exportCsv);
    }

    [Fact]
    public async Task ImportExportRoundTrip_Alliances_KeepsVerificationDetails()
    {
        var client = factory.CreateAdminClient();
        const string csv = """
            Code,Name,VerifiedAt,VerifiedBy,VerificationSource
            SA,Star Alliance,2026-01-02T03:04:05.0000000Z,prior-admin,manual
            """;

        var firstImport = await client.PostAsync("admin/import/alliances", BuildMultipart(csv));
        Assert.Equal(HttpStatusCode.OK, firstImport.StatusCode);

        var exportResponse = await client.GetAsync("admin/export/alliances");
        var exportCsv = await exportResponse.Content.ReadAsStringAsync();
        var exportedFields = FindDataRowFields(exportCsv, "SA,Star Alliance,");
        Assert.Equal(["2026-01-02T03:04:05.0000000Z", "prior-admin", "manual"], exportedFields[^3..]);

        // Round trip: re-import the exact CSV the export just produced. Because all three columns
        // are present, the row must keep them as-is rather than being re-stamped `import` by
        // whoever ran this second import.
        var secondImport = await client.PostAsync("admin/import/alliances",
            BuildMultipart(exportCsv, fileName: "alliances-export.csv"));
        Assert.Equal(HttpStatusCode.OK, secondImport.StatusCode);

        var reExportResponse = await client.GetAsync("admin/export/alliances");
        var reExportCsv = await reExportResponse.Content.ReadAsStringAsync();
        var reExportedFields = FindDataRowFields(reExportCsv, "SA,Star Alliance,");
        Assert.Equal(["2026-01-02T03:04:05.0000000Z", "prior-admin", "manual"], reExportedFields[^3..]);
    }

    [Fact]
    public async Task ImportExportRoundTrip_Lounges_KeepsVerificationDetails()
    {
        var client = factory.CreateAdminClient();

        const string airportsCsv = """
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt
            LHR,EGLL,Heathrow Airport,London,GB,51.4706,-0.461941,83
            """;
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync("admin/import/airports", BuildMultipart(airportsCsv))).StatusCode);

        const string providersCsv = """
            Code,Name,Kind,Description
            PLAZA,Plaza Premium,Contract,
            """;
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync("admin/import/lounge-providers", BuildMultipart(providersCsv))).StatusCode);

        const string loungesCsv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers,VerifiedAt,VerifiedBy,VerificationSource
            "Galleries Club",LHR,T5,,24h,true,false,false,,[],2026-01-02T03:04:05.0000000Z,prior-admin,manual
            """;
        var firstImport = await client.PostAsync("admin/import/lounges", BuildMultipart(loungesCsv));
        Assert.Equal(HttpStatusCode.OK, firstImport.StatusCode);

        var exportResponse = await client.GetAsync("admin/export/lounges");
        var exportCsv = await exportResponse.Content.ReadAsStringAsync();
        var exportedFields = FindDataRowFields(exportCsv, "Galleries Club,LHR,T5,");
        Assert.Equal(["2026-01-02T03:04:05.0000000Z", "prior-admin", "manual"], exportedFields[^3..]);

        var secondImport = await client.PostAsync("admin/import/lounges",
            BuildMultipart(exportCsv, fileName: "lounges-export.csv"));
        Assert.Equal(HttpStatusCode.OK, secondImport.StatusCode);

        var reExportResponse = await client.GetAsync("admin/export/lounges");
        var reExportCsv = await reExportResponse.Content.ReadAsStringAsync();
        var reExportedFields = FindDataRowFields(reExportCsv, "Galleries Club,LHR,T5,");
        Assert.Equal(["2026-01-02T03:04:05.0000000Z", "prior-admin", "manual"], reExportedFields[^3..]);
    }
}
