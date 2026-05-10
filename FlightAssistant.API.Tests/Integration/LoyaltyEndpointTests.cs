using System.Net;
using System.Net.Http.Json;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class LoyaltyEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task GetPrograms_ValidToken_Returns200()
    {
        var client = factory.CreateUserClient();
        var response = await client.GetAsync("loyalty/programs?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrograms_NoToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("loyalty/programs?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddMembership_PastExpiryDate_Returns400()
    {
        var client = factory.CreateUserClient();
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd");

        var response = await client.PostAsJsonAsync("loyalty/memberships", new
        {
            programId = 1,
            accountNumber = "ACC123",
            tierId = (int?)null,
            tierExpiryDate = yesterday,
            pointsExpiryDate = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TierExpiryDate", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddMembership_AccountNumberTooLong_Returns400()
    {
        var client = factory.CreateUserClient();

        var response = await client.PostAsJsonAsync("loyalty/memberships", new
        {
            programId = 1,
            accountNumber = new string('A', 65),
            tierId = (int?)null,
            tierExpiryDate = (string?)null,
            pointsExpiryDate = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetMemberships_ValidToken_Returns200()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        // Register traveler first so the handler can resolve the traveler
        await client.PostAsJsonAsync("travelers", new
        {
            email = $"{userId}@example.com",
            displayName = "Test",
            countryCode = (string?)null
        });

        var response = await client.GetAsync("loyalty/memberships?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
