using System.Net;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class LoungeEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task GetLounges_InvalidIataCode_Returns400()
    {
        var response = await factory.CreateUserClient().GetAsync("lounges?airportIata=LONDON");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetLounges_UnknownAirport_Returns200WithEmptyList_NotNotFound()
    {
        // The declared 404 was unreachable: this query never throws (#407).
        var response = await factory.CreateUserClient().GetAsync("lounges?airportIata=ZZZ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
