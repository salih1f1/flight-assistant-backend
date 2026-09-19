using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET</c>/<c>PUT /admin/providers</c> (#531). Hangs off the shared <c>/admin</c> group, so the
/// auth cases are covered by every other admin endpoint test class's own note on the identical
/// pattern (see <c>AdminJobEndpointTests</c>) rather than repeated here.
///
/// <para>
/// The API suite runs on the InMemory provider with the audit interceptor stripped (#319), so no
/// test here asserts an audit row -- these tests prove the route is wired and the status codes are
/// right. <c>SetProviderRouteCommandTests</c> (Application.Tests) is what proves the handler
/// writes exactly one audit row carrying the reason, against a substituted repository.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminProviderEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task GetProviders_ReturnsTheRouteSearchDecision()
    {
        var client = factory.CreateAdminClient();

        var rows = await client.GetFromJsonAsync<List<AdminProviderRouteDto>>("admin/providers");

        Assert.NotNull(rows);
        var route = Assert.Single(rows!, r => r.Job == "flight-search-route");
        Assert.Equal("Route search", route.DisplayName);
        Assert.Contains("aeroapi", route.Options);
        Assert.Contains("aerodatabox", route.Options);
    }

    [Fact]
    public async Task PutProvider_UnknownKey_Returns404()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync("admin/providers/nope",
            new SetProviderRouteRequest(new[] { "aeroapi" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PutProvider_ProviderThatIsNotAnOption_Returns400()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync("admin/providers/flight-search-route",
            new SetProviderRouteRequest(new[] { "opensky" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetProviders_ReturnsAllFourRoutingKeys()
    {
        var client = factory.CreateAdminClient();

        var rows = await client.GetFromJsonAsync<List<AdminProviderRouteDto>>("/admin/providers");

        Assert.Equal(4, rows!.Count);
        Assert.Contains(rows, r => r.Job == "flight-search-route");
        Assert.Contains(rows, r => r.Job == "verification-airport");
        Assert.Contains(rows, r => r.Job == "verification-airline");
        Assert.Contains(rows, r => r.Job == "verification-country");
    }
}
