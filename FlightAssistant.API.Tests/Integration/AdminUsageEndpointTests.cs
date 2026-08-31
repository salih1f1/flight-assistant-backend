using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class AdminUsageEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task GetAeroApiUsage_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("admin/usage/aeroapi");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAeroApiUsage_AuthenticatedWithoutAdminRole_Returns403()
    {
        var client = factory.CreateUserClient();

        var response = await client.GetAsync("admin/usage/aeroapi");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAeroApiUsage_AdminRole_Returns200()
    {
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/usage/aeroapi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAeroApiUsage_ExplicitDateRange_IsBoundFromQueryStringAndEchoedBack()
    {
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/usage/aeroapi?fromUtc=2026-01-01T00:00:00Z&toUtc=2026-02-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            DateTime.Parse("2026-01-01T00:00:00Z").ToUniversalTime(),
            body.GetProperty("fromUtc").GetDateTime());
        Assert.Equal(
            DateTime.Parse("2026-02-01T00:00:00Z").ToUniversalTime(),
            body.GetProperty("toUtc").GetDateTime());
        Assert.Equal(0, body.GetProperty("totalCalls").GetInt32());
    }

    [Fact]
    public async Task GetAeroApiUsage_FromUtcNotEarlierThanToUtc_Returns400()
    {
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/usage/aeroapi?fromUtc=2026-02-01T00:00:00Z&toUtc=2026-01-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAeroApiUsage_OnlyFromUtcSupplied_FarEnoughInThePastToExceedMaxRangeAgainstDefaultedToUtc_Returns400()
    {
        // The 366-day cap must be evaluated against the EFFECTIVE range, not just the supplied
        // bound: omitting ToUtc used to default it to "now" only AFTER the cap check ran, so a
        // FromUtc alone (no ToUtc at all) bypassed the cap entirely and could yield a decades-wide
        // range against the real table.
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/usage/aeroapi?fromUtc=2000-01-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
