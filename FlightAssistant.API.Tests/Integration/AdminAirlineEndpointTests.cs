using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The alliance-code guard (#449) end to end. These live at the API level rather than only beside
/// the handler unit tests because the defect they cover was a missing dependency, not wrong logic:
/// an unknown <c>AllianceCode</c> used to reach Postgres and fail
/// <c>FK_airlines_alliances_AllianceCode</c> as a 500. This suite runs on the InMemory provider,
/// which enforces no foreign key at all, so nothing here would ever have gone red on its own —
/// what these prove is that the guard is wired through DI and that the route answers 409.
/// </summary>
[Collection("Integration")]
public class AdminAirlineEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    /// <summary>
    /// Puts the country every airline below names in the table. Airlines carry a foreign key to
    /// <c>countries</c> and #455 now refuses one we do not have, so a create here has to satisfy
    /// that guard before it can exercise the alliance one.
    /// </summary>
    private static async Task GivenCountryAsync(HttpClient client)
    {
        var csv = "Code,Name,Alpha3,NumericCode,Region,Subregion,IsEU,IsSchengen,IsEEA\n"
                + "GB,United Kingdom,GBR,826,Europe,Northern Europe,false,false,false";
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        var form = new MultipartFormDataContent { { file, "file", "countries.csv" } };

        var response = await client.PostAsync("admin/import/countries", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Puts one alliance in the table, so a code exists for the accepted-code cases.</summary>
    private static async Task GivenAllianceAsync(HttpClient client, string code, string name)
    {
        var csv = $"Code,Name\n{code},{name}";
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        var form = new MultipartFormDataContent { { file, "file", "alliances.csv" } };

        var response = await client.PostAsync("admin/import/alliances", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateAirline_AllianceCodeThatIsNoAlliance_Returns409AsAProblemDocument()
    {
        var client = factory.CreateAdminClient();
        await GivenCountryAsync(client);

        var response = await client.PostAsJsonAsync("admin/airlines", new
        {
            iata = "Q1",
            icao = "QQA",
            name = "Guard Test Airways",
            countryCode = "GB",
            allianceCode = "NOPE",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("NOPE", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateAirline_AllianceCodeThatIsNoAlliance_Returns409()
    {
        var client = factory.CreateAdminClient();
        await GivenCountryAsync(client);
        var created = await client.PostAsJsonAsync("admin/airlines", new
        {
            iata = "Q2",
            icao = "QQB",
            name = "Guard Test Airways Two",
            countryCode = "GB",
            allianceCode = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CreatedAirline>())!.Id;

        var response = await client.PutAsJsonAsync($"admin/airlines/{id}", new
        {
            iata = "Q2",
            icao = "QQB",
            name = "Guard Test Airways Two",
            countryCode = "GB",
            allianceCode = "NOPE",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("NOPE", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateAirline_AllianceCodeThatIsAnAlliance_Returns204()
    {
        var client = factory.CreateAdminClient();
        await GivenCountryAsync(client);
        await GivenAllianceAsync(client, "OW", "oneworld");
        var created = await client.PostAsJsonAsync("admin/airlines", new
        {
            iata = "Q3",
            icao = "QQC",
            name = "Guard Test Airways Three",
            countryCode = "GB",
            allianceCode = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CreatedAirline>())!.Id;

        var response = await client.PutAsJsonAsync($"admin/airlines/{id}", new
        {
            iata = "Q3",
            icao = "QQC",
            name = "Guard Test Airways Three",
            countryCode = "GB",
            allianceCode = "OW",
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// A blank alliance code is "no alliance", not an unknown one — the admin panel's form sends the
    /// field empty when an airline has none, so refusing it would break every airline without one.
    /// </summary>
    [Fact]
    public async Task CreateAirline_BlankAllianceCode_Returns201()
    {
        var client = factory.CreateAdminClient();
        await GivenCountryAsync(client);

        var response = await client.PostAsJsonAsync("admin/airlines", new
        {
            iata = "Q4",
            icao = "QQD",
            name = "Guard Test Airways Four",
            countryCode = "GB",
            allianceCode = "",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private sealed record CreatedAirline(int Id);
}
