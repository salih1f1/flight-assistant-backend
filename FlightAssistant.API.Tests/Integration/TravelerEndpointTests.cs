using System.Net;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class TravelerEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task RegisterTraveler_ValidToken_Returns201()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        // No body — identity is derived from the token.
        var response = await client.PostAsync("travelers", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task RegisterTraveler_CalledTwice_IsIdempotent()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        var first  = await client.PostAsync("travelers", null);
        var second = await client.PostAsync("travelers", null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task GetProfile_NoToken_Returns401()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("travelers/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetProfile_AfterRegistration_Returns200()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        await client.PostAsync("travelers", null);

        var response = await client.GetAsync("travelers/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
