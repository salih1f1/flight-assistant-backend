using System.Net;
using System.Net.Http.Json;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// Confirms the 400 declared on ten admin routes (#411) is actually reachable: each route's
/// command carries a validator whose rule <c>{id:int}</c>/<c>{id:guid}</c> route constraints let
/// an HTTP caller violate — <c>0</c>/negative ints and the all-zero guid both bind, they just fail
/// <c>GreaterThan(0)</c> / <c>NotEqual(Guid.Empty)</c> once inside <c>ValidationBehaviour</c>.
/// </summary>
[Collection("Integration")]
public class AdminEndpointContractTests(FlightAssistantWebApplicationFactory factory)
{
    [Theory]
    [InlineData("admin/airlines/0")]
    [InlineData("admin/airports/0")]
    [InlineData("admin/lounges/0")]
    [InlineData("admin/loyalty/programs/0")]
    [InlineData("admin/travelers/00000000-0000-0000-0000-000000000000")]
    [InlineData("admin/travelers/00000000-0000-0000-0000-000000000000/device-tokens/00000000-0000-0000-0000-000000000000")]
    public async Task AdminDelete_NonPositiveOrEmptyId_Returns400(string url)
    {
        // Each of these routes' command ships a validator, and {id:int}/{id:guid} both match the
        // rejected values, so the 400 is reachable — it was simply undeclared (#411).
        var response = await factory.CreateAdminClient().DeleteAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminUpdateAirline_NonPositiveId_Returns400()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/airlines/0", new
        {
            iata = "BA", icao = "BAW", name = "British Airways", countryCode = "GB", allianceCode = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminUpdateAirport_NonPositiveId_Returns400()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/airports/0", new
        {
            iata = "LHR", icao = "EGLL", name = "Heathrow", city = "London", countryCode = "GB",
            latitude = 51.47, longitude = -0.4543, elevationFt = 83
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminUpdateLounge_NonPositiveId_Returns400()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/lounges/0", new
        {
            name = "Test Lounge", terminal = (string?)null, locationHint = (string?)null, hours = (string?)null,
            isAirside = true, isDomesticOnly = false, isInternationalOnly = false, amenities = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminUpdateLoyaltyProgram_NonPositiveId_Returns400()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/loyalty/programs/0", new
        {
            code = "BAEC", name = "Executive Club", kindId = 1, airlineId = (int?)null, allianceCode = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
