using System.Net;
using System.Net.Http.Headers;
using System.Text;

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
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt,Timezone
            LHR,EGLL,Heathrow Airport,London,GB,51.4706,-0.461941,83,Europe/London
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
}
