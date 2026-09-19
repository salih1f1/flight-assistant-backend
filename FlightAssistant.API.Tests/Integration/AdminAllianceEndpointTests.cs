using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The alliance detail drill-down (#457) end to end: that the route is mapped, resolves its
/// handler, and shapes the response the panel reads.
/// </summary>
[Collection("Integration")]
public class AdminAllianceEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private sealed record AllianceAirline(
        int Id, string? Iata, string? Icao, string Name, string CountryCode, string? LoyaltyProgramName);
    private sealed record AllianceDetail(int Id, string Code, string Name, List<AllianceAirline> Airlines);

    private static async Task ImportAsync(HttpClient client, string dataset, string csv)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        var form = new MultipartFormDataContent { { file, "file", $"{dataset}.csv" } };

        var response = await client.PostAsync($"admin/import/{dataset}", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Seeds one alliance with two airlines and returns that alliance's id, read back from the
    /// data browser: the import endpoints report counts, not ids, and the surrogate id is assigned
    /// by the database.
    /// </summary>
    private static async Task<int> GivenAllianceWithAirlinesAsync(HttpClient client)
    {
        await ImportAsync(client, "alliances", "Code,Name\nZZ,Test Alliance");
        await ImportAsync(client, "countries",
            "Code,Name,Alpha3,NumericCode,Region,Subregion,IsEU,IsSchengen,IsEEA\n"
            + "GB,United Kingdom,GBR,826,Europe,Northern Europe,false,false,false");
        await ImportAsync(client, "airlines",
            "Iata,Icao,Name,CountryCode,AllianceCode\n"
            + "Z1,ZZA,Zeta Airways,GB,ZZ\n"
            + "Z2,ZZB,Alpha Airways,GB,ZZ");
        // One of the two airlines runs a program and the other does not, so the response has to
        // carry both the name and the null.
        await ImportAsync(client, "loyalty-program-kinds", "Code,Name,Description\nAIRLINE,Airline,");
        await ImportAsync(client, "loyalty-programs",
            "Code,Name,KindCode,AirlineIata,AllianceCode,Tiers\n"
            + "ZZAP,Zeta Miles,AIRLINE,Z1,,[]");

        var rows = await client.GetFromJsonAsync<BrowsablePage>("admin/data/alliances?PageSize=200");
        var row = rows!.Items.Single(r => r["Code"].GetString() == "ZZ");
        return row["Id"].GetInt32();
    }

    private sealed record BrowsablePage(List<Dictionary<string, JsonElement>> Items);

    [Fact]
    public async Task GetAllianceDetail_ReturnsTheAllianceWithItsAirlinesOrderedByName()
    {
        var client = factory.CreateAdminClient();
        var id = await GivenAllianceWithAirlinesAsync(client);

        var detail = await client.GetFromJsonAsync<AllianceDetail>($"admin/alliances/{id}/detail");

        Assert.NotNull(detail);
        Assert.Equal("ZZ", detail.Code);
        Assert.Equal("Test Alliance", detail.Name);
        Assert.Equal(["Alpha Airways", "Zeta Airways"], detail.Airlines.Select(a => a.Name));
        Assert.Equal("Z2", detail.Airlines[0].Iata);
        Assert.Equal("GB", detail.Airlines[0].CountryCode);
    }

    /// <summary>
    /// The airline's own loyalty program travels with the row (#495): the panel shows this list on
    /// the alliance's record page, and the program is what a member airline is read for.
    /// </summary>
    [Fact]
    public async Task GetAllianceDetail_CarriesEachAirlinesLoyaltyProgramName()
    {
        var client = factory.CreateAdminClient();
        var id = await GivenAllianceWithAirlinesAsync(client);

        var detail = await client.GetFromJsonAsync<AllianceDetail>($"admin/alliances/{id}/detail");

        Assert.NotNull(detail);
        Assert.Equal("Zeta Miles", detail.Airlines.Single(a => a.Iata == "Z1").LoyaltyProgramName);
        // An airline with no program of its own is still on the list, with an empty cell.
        Assert.Null(detail.Airlines.Single(a => a.Iata == "Z2").LoyaltyProgramName);
    }

    [Fact]
    public async Task GetAllianceDetail_UnknownId_Returns404()
    {
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/alliances/987654/detail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
