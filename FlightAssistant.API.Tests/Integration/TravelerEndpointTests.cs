using System.Net;
using System.Net.Http.Json;

namespace FlightAssistant.API.Tests.Integration;

public class TravelerEndpointTests(FlightAssistantWebApplicationFactory factory)
    : IClassFixture<FlightAssistantWebApplicationFactory>
{
    [Fact]
    public async Task RegisterTraveler_ValidToken_Returns201()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        var response = await client.PostAsJsonAsync("travelers", new
        {
            email = $"{userId}@example.com",
            displayName = "Test User",
            countryCode = (string?)null
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task RegisterTraveler_CalledTwice_IsIdempotent()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);
        var body = new { email = $"{userId}@example.com", displayName = "Test User", countryCode = (string?)null };

        var first  = await client.PostAsJsonAsync("travelers", body);
        var second = await client.PostAsJsonAsync("travelers", body);

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

        await client.PostAsJsonAsync("travelers", new
        {
            email = $"{userId}@example.com",
            displayName = "Test User",
            countryCode = (string?)null
        });

        var response = await client.GetAsync("travelers/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegisterTraveler_InvalidEmail_Returns400WithValidationDetails()
    {
        var client = factory.CreateUserClient();
        var response = await client.PostAsJsonAsync("travelers", new
        {
            email = "not-an-email",
            displayName = (string?)null,
            countryCode = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Email", body, StringComparison.OrdinalIgnoreCase);
    }
}
