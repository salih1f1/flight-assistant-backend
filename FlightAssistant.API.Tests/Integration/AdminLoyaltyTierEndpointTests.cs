using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// Writing a loyalty program's tiers (#488) end to end: that the routes are mapped, that a tier is
/// reachable only through the program that owns it, and that the unique indexes on the table are
/// reported as conflicts rather than reaching Postgres as 500s.
/// </summary>
[Collection("Integration")]
public class AdminLoyaltyTierEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private sealed record Tier(int Id, string Code, string Name, int Rank, string? AllianceCode, string? AllianceTierCode);
    private sealed record Program(int Id, string Code, string Name, List<Tier> Tiers);
    private sealed record ProgramPage(List<Program> Items);

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTier_Returns201AndTheTierComesBackOnTheProgram()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();

        var response = await client.PostAsJsonAsync($"admin/loyalty/programs/{programId}/tiers", new
        {
            code = "GOLD",
            name = "Gold",
            rank = 3,
            allianceCode = "STAR",
            allianceTierCode = "*G"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Assert.True(id > 0, "The created tier should come back with the id the database assigned.");

        var tier = Assert.Single(await GetTiersAsync(client, programId));
        Assert.Equal(id, tier.Id);
        Assert.Equal("GOLD", tier.Code);
        Assert.Equal("Gold", tier.Name);
        Assert.Equal(3, tier.Rank);
        Assert.Equal("STAR", tier.AllianceCode);
        Assert.Equal("*G", tier.AllianceTierCode);
    }

    [Fact]
    public async Task CreateTier_UnknownProgram_Returns404()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("admin/loyalty/programs/987654/tiers", new
        {
            code = "GOLD",
            name = "Gold",
            rank = 3
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTier_CodeAlreadyOnTheProgram_Returns409()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();
        await CreateTierAsync(client, programId, "GOLD", "Gold", 3);

        var response = await client.PostAsJsonAsync($"admin/loyalty/programs/{programId}/tiers", new
        {
            code = "GOLD",
            name = "Gold again",
            rank = 4
        });

        // loyalty_tiers is unique on (ProgramId, Code): without the handler's check this would
        // reach Postgres and fail its index as a 500.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateTier_RankAlreadyOnTheProgram_Returns409()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();
        await CreateTierAsync(client, programId, "GOLD", "Gold", 3);

        var response = await client.PostAsJsonAsync($"admin/loyalty/programs/{programId}/tiers", new
        {
            code = "SILVER",
            name = "Silver",
            rank = 3
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateTier_EmptyCode_Returns400()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();

        var response = await client.PostAsJsonAsync($"admin/loyalty/programs/{programId}/tiers", new
        {
            code = "",
            name = "Gold",
            rank = 3
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The same code on a different program is not a clash: the index is per program.</summary>
    [Fact]
    public async Task CreateTier_CodeUsedByAnotherProgram_Returns201()
    {
        var client = factory.CreateAdminClient();
        var first = await SeedProgramAsync();
        var second = await SeedProgramAsync();
        await CreateTierAsync(client, first, "GOLD", "Gold", 3);

        var response = await client.PostAsJsonAsync($"admin/loyalty/programs/{second}/tiers", new
        {
            code = "GOLD",
            name = "Gold",
            rank = 3
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTier_Returns204AndChangesTheTier()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();
        var tierId = await CreateTierAsync(client, programId, "GOLD", "Gold", 3);

        var response = await client.PutAsJsonAsync($"admin/loyalty/programs/{programId}/tiers/{tierId}", new
        {
            code = "GOLD",
            name = "Gold Elite",
            rank = 4,
            allianceCode = "OW",
            allianceTierCode = "EMD"
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var tier = Assert.Single(await GetTiersAsync(client, programId));
        Assert.Equal("Gold Elite", tier.Name);
        Assert.Equal(4, tier.Rank);
        Assert.Equal("OW", tier.AllianceCode);
        Assert.Equal("EMD", tier.AllianceTierCode);
    }

    /// <summary>
    /// The tier is looked up inside the program named in the route, so another program's tier is
    /// not found — one program's route can't be used to edit another's.
    /// </summary>
    [Fact]
    public async Task UpdateTier_TierOfAnotherProgram_Returns404AndLeavesItAlone()
    {
        var client = factory.CreateAdminClient();
        var owner = await SeedProgramAsync();
        var other = await SeedProgramAsync();
        var tierId = await CreateTierAsync(client, owner, "GOLD", "Gold", 3);

        var response = await client.PutAsJsonAsync($"admin/loyalty/programs/{other}/tiers/{tierId}", new
        {
            code = "TAKEN",
            name = "Taken over",
            rank = 9
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Gold", (await GetTiersAsync(client, owner)).Single().Name);
    }

    [Fact]
    public async Task UpdateTier_UnknownTier_Returns404()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();

        var response = await client.PutAsJsonAsync($"admin/loyalty/programs/{programId}/tiers/987654", new
        {
            code = "GOLD",
            name = "Gold",
            rank = 3
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTier_CodeTakenByAnotherTierOfTheProgram_Returns409()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();
        await CreateTierAsync(client, programId, "GOLD", "Gold", 3);
        var silver = await CreateTierAsync(client, programId, "SILVER", "Silver", 2);

        var response = await client.PutAsJsonAsync($"admin/loyalty/programs/{programId}/tiers/{silver}", new
        {
            code = "GOLD",
            name = "Silver",
            rank = 2
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// Renaming a tier without touching its code or rank must not report a conflict with itself —
    /// the clash check excludes the tier being edited.
    /// </summary>
    [Fact]
    public async Task UpdateTier_KeepingItsOwnCodeAndRank_Returns204()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();
        var tierId = await CreateTierAsync(client, programId, "GOLD", "Gold", 3);

        var response = await client.PutAsJsonAsync($"admin/loyalty/programs/{programId}/tiers/{tierId}", new
        {
            code = "GOLD",
            name = "Gold renamed",
            rank = 3
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteTier_Returns204AndTheTierIsGone()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();
        var gold = await CreateTierAsync(client, programId, "GOLD", "Gold", 3);
        await CreateTierAsync(client, programId, "SILVER", "Silver", 2);

        var response = await client.DeleteAsync($"admin/loyalty/programs/{programId}/tiers/{gold}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["SILVER"], (await GetTiersAsync(client, programId)).Select(t => t.Code));
    }

    [Fact]
    public async Task DeleteTier_UnknownTier_Returns404()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();

        var response = await client.DeleteAsync($"admin/loyalty/programs/{programId}/tiers/987654");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteTier_TierOfAnotherProgram_Returns404AndLeavesItAlone()
    {
        var client = factory.CreateAdminClient();
        var owner = await SeedProgramAsync();
        var other = await SeedProgramAsync();
        var tierId = await CreateTierAsync(client, owner, "GOLD", "Gold", 3);

        var response = await client.DeleteAsync($"admin/loyalty/programs/{other}/tiers/{tierId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await GetTiersAsync(client, owner));
    }

    /// <summary>
    /// Deleting a tier travelers hold is allowed, and leaves their memberships standing: the
    /// endpoint neither refuses nor fails on the reference. The panel's delete confirm says as much.
    ///
    /// What this test does NOT pin is the TierId going null, though that is what happens in
    /// production: the column is ON DELETE SET NULL
    /// (FK_traveler_loyalty_memberships_loyalty_tiers_TierId, InitLoyalty) and Postgres applies it
    /// as part of the delete. These tests run on the EF in-memory provider, which enforces no
    /// foreign keys at all and can only apply SetNull to memberships already tracked by the same
    /// DbContext — so asserting null here would assert the provider, not the schema.
    /// </summary>
    [Fact]
    public async Task DeleteTier_HeldByAMembership_SucceedsAndKeepsTheMembership()
    {
        var client = factory.CreateAdminClient();
        var programId = await SeedProgramAsync();
        var tierId = await CreateTierAsync(client, programId, "GOLD", "Gold", 3);
        var membershipId = await SeedMembershipAsync(programId, tierId);

        var response = await client.DeleteAsync($"admin/loyalty/programs/{programId}/tiers/{tierId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await GetTiersAsync(client, programId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        Assert.True(
            await db.TravelerLoyaltyMemberships.AsNoTracking().AnyAsync(m => m.Id == membershipId),
            "Deleting a tier must not take the memberships that pointed at it with it.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A loyalty program with a code no other test uses — the "Integration" collection shares one
    /// database across every class in it, and these tests read a program's tiers back by id.
    /// </summary>
    private async Task<int> SeedProgramAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

        var marker = Guid.NewGuid().ToString("N")[..8];
        var kind = LoyaltyProgramKind.Create($"K{marker}", "Tier test kind");
        db.LoyaltyProgramKinds.Add(kind);
        await db.SaveChangesAsync();

        var program = LoyaltyProgram.Create($"P{marker}", "Tier test program", kind.Id);
        db.LoyaltyPrograms.Add(program);
        await db.SaveChangesAsync();
        return program.Id;
    }

    /// <summary>A traveler holding the given tier, for the delete-with-membership test.</summary>
    private async Task<Guid> SeedMembershipAsync(int programId, int tierId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

        // The id-less overload registers with Guid.Empty, which the shared database already holds.
        var traveler = Traveler.Register(Guid.NewGuid(), $"{Guid.NewGuid():N}@example.com", "Tier test traveler");
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var membership = TravelerLoyaltyMembership.Create(traveler.Id, programId, "ACC123", tierId, null);
        db.TravelerLoyaltyMemberships.Add(membership);
        await db.SaveChangesAsync();
        return membership.Id;
    }

    private static async Task<int> CreateTierAsync(HttpClient client, int programId, string code, string name, int rank)
    {
        var response = await client.PostAsJsonAsync($"admin/loyalty/programs/{programId}/tiers", new { code, name, rank });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    /// <summary>
    /// The program's tiers, read back from the list endpoint: it is the only route that returns
    /// them, and reading through it proves the write reached the database.
    /// </summary>
    private static async Task<List<Tier>> GetTiersAsync(HttpClient client, int programId)
    {
        var page = await client.GetFromJsonAsync<ProgramPage>("admin/loyalty/programs?Page=1&PageSize=200");
        return page!.Items.Single(p => p.Id == programId).Tiers;
    }
}
