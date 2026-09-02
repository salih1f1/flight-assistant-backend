using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/data/tables</c> and <c>GET /admin/data/{table}</c> hang off the shared
/// <c>/admin</c> group, so the auth cases here are really checking that they were mapped onto that
/// group rather than standalone.
///
/// <para>
/// Rows are seeded directly against <see cref="FlightAssistantDbContext"/> via the aggregates'
/// own factory methods — never through a write endpoint, since this task adds none. The
/// "Integration" collection shares one <see cref="FlightAssistantWebApplicationFactory"/> — and
/// therefore one InMemory database — across every test class in the collection (see
/// <c>AdminAuditEndpointTests</c>'s own note on this), so every test here either narrows with a
/// fresh-per-test marker or asserts a delta rather than an absolute count.
/// </para>
///
/// <para>
/// <b>The masking tests are the security-critical ones in this file.</b> Each asserts against the
/// <em>raw response body string</em>, not a deserialized DTO — a deserialization step could itself
/// hide a leak (e.g. a property the test forgot to read back) — and each includes a
/// deliberately-unmasked column read from the very same response, so a passing assertion actually
/// discriminates: an assertion that would pass whether or not masking worked (e.g. only checking
/// that some string is absent, without ever confirming the row was found at all) proves nothing.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminDataEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static string Marker() => Guid.NewGuid().ToString("N");

    private async Task SeedAsync(Action<FlightAssistantDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task<string> GetRawBodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ── Auth ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTables_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("admin/data/tables");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTables_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().GetAsync("admin/data/tables");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTables_Admin_Returns200()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/data/tables");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetTableData_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("admin/data/travelers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTableData_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().GetAsync("admin/data/travelers");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTableData_Admin_Returns200()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/data/travelers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── GET /admin/data/tables ───────────────────────────────────────────────────

    [Fact]
    public async Task GetTables_ListsTheTravelersTable_WithSlugAndDisplayName()
    {
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/data/tables");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tables = await response.Content.ReadFromJsonAsync<List<BrowsableTableDto>>();
        Assert.NotNull(tables);
        var travelers = Assert.Single(tables!, t => t.Slug == "travelers");
        Assert.Equal("Travelers", travelers.DisplayName);
    }

    /// <summary>
    /// Row counts are correct after seeding — checked as a delta rather than an absolute count,
    /// since the InMemory database is shared with every other test class in the "Integration"
    /// collection (which run sequentially against each other, never concurrently with this test,
    /// so the delta itself is race-free).
    /// </summary>
    [Fact]
    public async Task GetTables_RowCount_ReflectsRowsSeededSinceTheLastCall()
    {
        var client = factory.CreateAdminClient();

        async Task<int> TravelersRowCount()
        {
            var response = await client.GetAsync("admin/data/tables");
            var tables = await response.Content.ReadFromJsonAsync<List<BrowsableTableDto>>();
            return Assert.Single(tables!, t => t.Slug == "travelers").RowCount;
        }

        var before = await TravelersRowCount();

        var marker = Marker();
        await SeedAsync(db => db.Travelers.AddRange(
            Traveler.Register($"{marker}-1@example.com"),
            Traveler.Register($"{marker}-2@example.com"),
            Traveler.Register($"{marker}-3@example.com")));

        var after = await TravelersRowCount();

        Assert.Equal(before + 3, after);
    }

    // ── Unknown table / column ──────────────────────────────────────────────────

    [Fact]
    public async Task GetTableData_UnknownTableSlug_Returns404()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/data/not-a-real-table");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTableData_UnknownSortColumn_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/data/travelers?sort=NotARealColumn");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("NotARealColumn", body);
    }

    [Fact]
    public async Task GetTableData_UnknownFilterColumn_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/data/travelers?filter=NotARealColumn:x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("NotARealColumn", body);
    }

    [Fact]
    public async Task GetTableData_FilterWithNoColon_IsAMalformedFilter_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/data/travelers?filter=DisplayName");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Sensitive column in sort/filter ─────────────────────────────────────────

    [Fact]
    public async Task GetTableData_SensitiveColumnInSort_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync($"admin/data/travelers?sort={nameof(Traveler.Email)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(Traveler.Email), body);
    }

    /// <summary>
    /// Otherwise the browser becomes an oracle for guessing a masked value one character at a
    /// time: a matching filter returns a row, a non-matching one returns none, and that single bit
    /// is enough to binary-search a value out of the table without ever seeing it rendered.
    /// </summary>
    [Fact]
    public async Task GetTableData_SensitiveColumnInFilter_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync($"admin/data/travelers?filter={nameof(Traveler.Email)}:a");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(Traveler.Email), body);
    }

    [Fact]
    public async Task GetTableData_SensitiveColumnInSort_DeviceTokens_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync($"admin/data/device-tokens?sort={nameof(DeviceToken.Token)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetTableData_SensitiveColumnInFilter_TravelerLoyaltyMemberships_Returns400()
    {
        var response = await factory.CreateAdminClient()
            .GetAsync($"admin/data/traveler-loyalty-memberships?filter={nameof(TravelerLoyaltyMembership.AccountNumber)}:1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Masking: the security-critical tests ────────────────────────────────────

    /// <summary>
    /// A list response never contains a full traveler email. <see cref="Traveler.DisplayName"/> is
    /// unmasked by design and deliberately checked in the same response — proof this assertion
    /// discriminates: if masking were entirely broken (nothing ever redacted, or the endpoint
    /// returned empty rows) this test would still need both halves to pass, not just the absence
    /// check alone.
    /// </summary>
    [Fact]
    public async Task GetTableData_Travelers_NeverContainsTheRawEmail_ButDoesContainTheUnmaskedDisplayName()
    {
        var marker = Marker();
        var rawEmail = $"very-secret-{marker}@example.com";
        var displayName = $"Findable Traveler {marker}";
        await SeedAsync(db => db.Travelers.Add(Traveler.Register(rawEmail, displayName)));

        var body = await GetRawBodyAsync(
            factory.CreateAdminClient(), $"admin/data/travelers?filter=DisplayName:{Uri.EscapeDataString(marker)}");

        Assert.Contains(displayName, body);
        Assert.Contains("<redacted>", body);
        Assert.DoesNotContain(rawEmail, body);

        var result = JsonSerializer.Deserialize<PagedResult<Dictionary<string, JsonElement>>>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        var row = Assert.Single(result!.Items);
        Assert.Equal("<redacted>", row[nameof(Traveler.Email)].GetString());
        Assert.Equal(displayName, row[nameof(Traveler.DisplayName)].GetString());
    }

    /// <summary>A list response never contains a full device token. <c>Platform</c> is not
    /// sensitive and is checked unmasked in the same response for the same discriminating
    /// reason as the travelers test above.</summary>
    [Fact]
    public async Task GetTableData_DeviceTokens_NeverContainsTheRawToken_ButDoesContainTheUnmaskedPlatform()
    {
        var traveler = Traveler.Register($"{Guid.NewGuid():N}@example.com");
        var rawToken = $"very-secret-device-token-{Guid.NewGuid():N}";
        traveler.AddDeviceToken("android", rawToken);
        await SeedAsync(db => db.Travelers.Add(traveler));

        var body = await GetRawBodyAsync(
            factory.CreateAdminClient(), $"admin/data/device-tokens?filter=TravelerId:{traveler.Id}");

        Assert.Contains("android", body);
        Assert.Contains("<redacted>", body);
        Assert.DoesNotContain(rawToken, body);

        var result = JsonSerializer.Deserialize<PagedResult<Dictionary<string, JsonElement>>>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        var row = Assert.Single(result!.Items);
        Assert.Equal("<redacted>", row[nameof(DeviceToken.Token)].GetString());
        Assert.Equal("android", row[nameof(DeviceToken.Platform)].GetString());
    }

    /// <summary>A list response never contains a full loyalty account number. <c>TierId</c> is not
    /// sensitive and is checked unmasked in the same response for the same discriminating reason
    /// as the two tests above.</summary>
    [Fact]
    public async Task GetTableData_TravelerLoyaltyMemberships_NeverContainsTheRawAccountNumber_ButDoesContainTheUnmaskedTierId()
    {
        var travelerId = Guid.NewGuid();
        var rawAccountNumber = $"ACCT-SECRET-{Guid.NewGuid():N}";
        await SeedAsync(db => db.TravelerLoyaltyMemberships.Add(
            TravelerLoyaltyMembership.Create(travelerId, programId: 1, accountNumber: rawAccountNumber, tierId: 7)));

        var body = await GetRawBodyAsync(
            factory.CreateAdminClient(), $"admin/data/traveler-loyalty-memberships?filter=TravelerId:{travelerId}");

        Assert.Contains("<redacted>", body);
        Assert.DoesNotContain(rawAccountNumber, body);

        var result = JsonSerializer.Deserialize<PagedResult<Dictionary<string, JsonElement>>>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        var row = Assert.Single(result!.Items);
        Assert.Equal("<redacted>", row[nameof(TravelerLoyaltyMembership.AccountNumber)].GetString());
        Assert.Equal(7, row[nameof(TravelerLoyaltyMembership.TierId)].GetInt32());
    }
}
