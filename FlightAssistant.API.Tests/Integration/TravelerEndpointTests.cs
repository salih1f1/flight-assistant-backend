using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class TravelerEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static async Task<(string? Email, string? DisplayName)> ReadProfileAsync(HttpClient client)
    {
        var response = await client.GetAsync("travelers/me");
        response.EnsureSuccessStatusCode();
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (profile.GetProperty("email").GetString(), profile.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task RegisterTraveler_ValidToken_Returns201()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

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
    public async Task RegisterTraveler_StoresTheIdentityCarriedByTheToken()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        await client.PostAsync("travelers", null);

        var (email, displayName) = await ReadProfileAsync(client);
        Assert.Equal($"{userId}@example.com", email);
        Assert.Equal(TestAuthHandler.DisplayName, displayName);
    }

    // The defect this endpoint was changed for (#544): the caller, not the token, decided what
    // identity was stored, so any authenticated traveler could write an address that appears
    // nowhere in their token.
    [Fact]
    public async Task RegisterTraveler_BodyClaimingAnotherIdentity_IsIgnored()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        var response = await client.PostAsJsonAsync("travelers", new
        {
            email = "someone.else@evil.example",
            displayName = "Someone Else"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (email, displayName) = await ReadProfileAsync(client);
        Assert.Equal($"{userId}@example.com", email);
        Assert.Equal(TestAuthHandler.DisplayName, displayName);
    }

    // A second login re-reads the token, so a row registered under a stale address is corrected
    // without a backfill — the reason the fix needs no migration.
    [Fact]
    public async Task RegisterTraveler_TokenIdentityChanged_IsReconciledOnTheNextCall()
    {
        var userId = Guid.NewGuid().ToString();
        await factory.CreateUserClient(userId).PostAsync("travelers", null);

        var renamed = factory.CreateUserClient(userId, email: "renamed@example.com", displayName: "Renamed");
        await renamed.PostAsync("travelers", null);

        var (email, displayName) = await ReadProfileAsync(renamed);
        Assert.Equal("renamed@example.com", email);
        Assert.Equal("Renamed", displayName);
    }

    [Fact]
    public async Task RegisterTraveler_TokenWithNoEmailClaim_Returns401()
    {
        var client = factory.CreateEmaillessUserClient();

        var response = await client.PostAsync("travelers", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A phone sign-in puts a number in preferred_username, which is the one shape that still
    // reaches RegisterTravelerCommandValidator — so the route's declared 400 stays reachable.
    [Fact]
    public async Task RegisterTraveler_TokenEmailClaimIsNotAnAddress_Returns400WithValidationDetails()
    {
        var client = factory.CreateUserClient(email: "not-an-email");

        var response = await client.PostAsync("travelers", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Email", body, StringComparison.OrdinalIgnoreCase);
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
        var client = factory.CreateUserClient();

        await client.PostAsync("travelers", null);

        var response = await client.GetAsync("travelers/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
