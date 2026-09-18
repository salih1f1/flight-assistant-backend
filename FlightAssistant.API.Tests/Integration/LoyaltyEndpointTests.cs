using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.DependencyInjection;

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
            tierExpiryDate = yesterday
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
            tierExpiryDate = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddMembership_BodyStillCarriesPointsExpiry_Returns201()
    {
        // Clients written against the old contract keep sending pointsExpiryDate. The field is gone
        // from the request record, and an unknown JSON property is ignored rather than rejected, so
        // those callers must go on working (#484).
        var client = factory.CreateUserClient(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("loyalty/memberships", new
        {
            programId = await SeedLoyaltyProgramAsync(),
            accountNumber = "ACC123",
            tierId = (int?)null,
            tierExpiryDate = (string?)null,
            pointsExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)).ToString("yyyy-MM-dd")
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    [Fact]
    public async Task RemoveMembership_Unknown_Returns204_Idempotently()
    {
        // The declared 404 was unreachable — the handler returns early (#407).
        var response = await factory.CreateUserClient().DeleteAsync($"loyalty/memberships/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RemoveMembership_EmptyGuid_Returns400()
    {
        var response = await factory.CreateUserClient().DeleteAsync($"loyalty/memberships/{Guid.Empty}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A loyalty program to hold a membership against, with a code no other test uses — the
    /// "Integration" collection shares one in-memory database across every class in it. Since #475
    /// the add refuses a program we don't have, so a membership needs a real one.
    /// </summary>
    private async Task<int> SeedLoyaltyProgramAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

        var marker = Guid.NewGuid().ToString("N")[..8];
        var kind = LoyaltyProgramKind.Create($"K{marker}", "Loyalty test kind");
        db.LoyaltyProgramKinds.Add(kind);
        await db.SaveChangesAsync();

        var program = LoyaltyProgram.Create($"P{marker}", "Loyalty test program", kind.Id);
        db.LoyaltyPrograms.Add(program);
        await db.SaveChangesAsync();
        return program.Id;
    }

    [Fact]
    public async Task AddMembership_UnknownProgram_Returns409()
    {
        var client = factory.CreateUserClient();

        var response = await client.PostAsJsonAsync("loyalty/memberships", new
        {
            programId = 987654,
            accountNumber = "ACC123",
            tierId = (int?)null,
            tierExpiryDate = (string?)null
        });

        // Before #475 this reached Postgres and failed its foreign key as a 500.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task<Guid> AddMembershipAsAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("loyalty/memberships", new
        {
            programId = await SeedLoyaltyProgramAsync(),
            accountNumber = "ACC123",
            tierId = (int?)null,
            tierExpiryDate = (string?)null
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task UpdateMembership_AnotherTravelersMembership_Returns403()
    {
        // Pins the 403 this branch declared (#407): without this test, a repository scoped by
        // traveler — the obvious refactor of GetMembershipByIdAsync — would silently turn this
        // into a 404 with the suite still green.
        var owner = factory.CreateUserClient();
        var membershipId = await AddMembershipAsAsync(owner);

        var other = factory.CreateUserClient();
        var response = await other.PutAsJsonAsync($"loyalty/memberships/{membershipId}", new
        {
            accountNumber = "ACC999",
            tierId = (int?)null,
            tierExpiryDate = (string?)null
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RemoveMembership_AnotherTravelersMembership_Returns403()
    {
        // Pins the 403 this branch declared (#407): without this test, a repository scoped by
        // traveler — the obvious refactor of GetMembershipByIdAsync — would silently turn this
        // into a 204 with the suite still green.
        var owner = factory.CreateUserClient();
        var membershipId = await AddMembershipAsAsync(owner);

        var other = factory.CreateUserClient();
        var response = await other.DeleteAsync($"loyalty/memberships/{membershipId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
