using System.Net;
using System.Text.Json;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class TravelerEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task RegisterTraveler_ValidToken_Returns201()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        // No request body — identity is read from the verified token.
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
    public async Task GetProfile_AfterRegistration_ReturnsTokenIdentity()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        await client.PostAsync("travelers", null);

        var response = await client.GetAsync("travelers/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Email and display name are sourced from the token, not from any client input.
        Assert.Equal($"{userId}@example.com", root.GetProperty("email").GetString());
        Assert.Equal("Test User", root.GetProperty("displayName").GetString());
    }
}
