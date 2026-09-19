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

        var map = await client.GetFromJsonAsync<AdminProviderMapDto>("admin/providers");

        Assert.NotNull(map);
        var route = Assert.Single(map!.Routes, r => r.Job == "flight-search-route");
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

        var map = await client.GetFromJsonAsync<AdminProviderMapDto>("/admin/providers");

        var rows = map!.Routes;
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, r => r.Job == "flight-search-route");
        Assert.Contains(rows, r => r.Job == "verification-airport");
        Assert.Contains(rows, r => r.Job == "verification-airline");
        Assert.Contains(rows, r => r.Job == "verification-country");
    }

    [Fact]
    public async Task GetProviders_ReturnsTheOperationMapAndHeadroom()
    {
        var client = factory.CreateAdminClient();

        var map = await client.GetFromJsonAsync<AdminProviderMapDto>("/admin/providers");

        Assert.NotNull(map);
        Assert.Equal(4, map!.Routes.Count);

        // Every trigger the operator asked about is represented.
        Assert.Contains(map.Operations, o => o.Operation.StartsWith("POST /flights"));
        Assert.Contains(map.Operations, o => o.Trigger == "Status sync");
        Assert.Contains(map.Operations, o => o.Trigger == "Verification");

        // A fixed row explains itself rather than just being absent from the switch list.
        var flightNumberSearch = Assert.Single(map.Operations,
            o => o.Operation.Contains("anchored on a flight number"));
        Assert.Null(flightNumberSearch.RoutingKey);
        Assert.Contains("6E4120", flightNumberSearch.FixedReason!);

        // Quota is unknown, not zero, in a test host that has called AeroDataBox never.
        var aeroDataBox = Assert.Single(map.Headroom, h => h.Provider == "aerodatabox");
        Assert.Null(aeroDataBox.QuotaRemaining);
    }
}
