using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// M7: the unknown-slug path is thrown as a <c>DomainException</c> rather than returned as a
    /// bare <c>Results.NotFound()</c>, specifically so it carries a real ProblemDetails body (with
    /// a <c>traceId</c>) matching the endpoint's declared <c>.ProducesProblem(404)</c> — an empty
    /// 404 body would satisfy only the status-code assertion, not the contract.
    /// </summary>
    [Fact]
    public async Task GetTableData_UnknownTableSlug_Returns404WithProblemDetailsBody()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/data/not-a-real-table");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not-a-real-table", body);
        Assert.Contains("traceId", body);
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

    // ── POST /admin/data/{table}/{id}/reveal (#318) ─────────────────────────────

    private async Task<int> AuditRowCountForEntityIdAsync(string entityId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        return await db.AdminAuditLogs.CountAsync(a => a.EntityId == entityId);
    }

    private static Task<HttpResponseMessage> RevealAsync(HttpClient client, string table, object id, string column) =>
        client.PostAsJsonAsync($"admin/data/{table}/{id}/reveal", new RevealValueRequest(column));

    // ── Auth ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reveal_Anonymous_Returns401()
    {
        var response = await RevealAsync(factory.CreateClient(), "travelers", Guid.NewGuid(), nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reveal_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await RevealAsync(factory.CreateUserClient(), "travelers", Guid.NewGuid(), nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Unknown table / id / column ──────────────────────────────────────────────

    /// <summary>No audit row for a 404: the table doesn't even resolve, so nothing about a reveal
    /// was ever attempted against real data.</summary>
    [Fact]
    public async Task Reveal_Admin_UnknownTableSlug_Returns404_AndWritesNoAuditRow()
    {
        var id = Guid.NewGuid().ToString();

        var response = await RevealAsync(factory.CreateAdminClient(), "not-a-real-table", id, "Email");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await AuditRowCountForEntityIdAsync(id));
    }

    /// <summary>The ordering rule #318 exists for, proven end-to-end: an id that resolves to no
    /// row must 404 without ever constructing — let alone committing — an audit row.</summary>
    [Fact]
    public async Task Reveal_Admin_UnknownId_Returns404_AndWritesNoAuditRow()
    {
        var id = Guid.NewGuid().ToString();

        var response = await RevealAsync(factory.CreateAdminClient(), "travelers", id, nameof(Traveler.Email));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await AuditRowCountForEntityIdAsync(id));
    }

    /// <summary>An ordinary, already-unmasked column is rejected exactly like a made-up one — a
    /// reveal is not a second way to read a row.</summary>
    [Fact]
    public async Task Reveal_Admin_NonSensitiveColumn_Returns400_AndWritesNoAuditRow()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com", "Findable Name");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await RevealAsync(factory.CreateAdminClient(), "travelers", traveler.Id, nameof(Traveler.DisplayName));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(Traveler.DisplayName), body);
        Assert.Equal(0, await AuditRowCountForEntityIdAsync(traveler.Id.ToString()));
    }

    [Fact]
    public async Task Reveal_Admin_UnknownColumn_Returns400_AndWritesNoAuditRow()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await RevealAsync(factory.CreateAdminClient(), "travelers", traveler.Id, "NotARealColumn");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await AuditRowCountForEntityIdAsync(traveler.Id.ToString()));
    }

    // ── Successful reveal ────────────────────────────────────────────────────────

    /// <summary>The mirror image of the masking tests above: this is the one endpoint where the
    /// raw value is expected to leave the API.</summary>
    [Fact]
    public async Task Reveal_Admin_SensitiveColumn_Returns200_WithTheRawUnmaskedValue()
    {
        var rawEmail = $"very-secret-{Marker()}@example.com";
        var traveler = Traveler.Register(rawEmail);
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await RevealAsync(factory.CreateAdminClient(), "travelers", traveler.Id, nameof(Traveler.Email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(rawEmail, body);
        Assert.DoesNotContain("<redacted>", body);

        var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        Assert.Equal("travelers", result!["table"].GetString());
        Assert.Equal(traveler.Id.ToString(), result["id"].GetString());
        Assert.Equal(nameof(Traveler.Email), result["column"].GetString());
        Assert.Equal(rawEmail, result["value"].GetString());
    }

    [Fact]
    public async Task Reveal_Admin_SuccessfulReveal_ResponseCarriesCacheControlNoStore()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await RevealAsync(factory.CreateAdminClient(), "travelers", traveler.Id, nameof(Traveler.Email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    /// <summary>
    /// The security-critical audit-content assertions (#318), proven end-to-end through the real
    /// endpoint rather than only at the handler level: exactly one row, the right actor/entity/
    /// action, and — checked against the row's whole serialized form, with the column name as a
    /// control so the "value absent" half of this assertion is proven to discriminate — the
    /// revealed value appears nowhere in it.
    /// </summary>
    [Fact]
    public async Task Reveal_Admin_SuccessfulReveal_WritesExactlyOneAuditRow_WithNoRevealedValueAnywhereInIt()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var rawEmail = $"very-secret-{Marker()}@example.com";
        var traveler = Traveler.Register(rawEmail);
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await RevealAsync(
            factory.CreateAdminClient(actorId), "travelers", traveler.Id, nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var rows = await db.AdminAuditLogs
            .Where(a => a.EntityId == traveler.Id.ToString())
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(AdminAuditAction.Reveal, row.Action);
        Assert.Equal(actorId, row.ActorId);
        Assert.Equal(nameof(Traveler), row.EntityType);
        Assert.Null(row.Before);
        Assert.Null(row.After);

        var serialized = JsonSerializer.Serialize(row);
        Assert.Contains(nameof(Traveler.Email), serialized); // control: proves this check sees real content
        Assert.DoesNotContain(rawEmail, serialized);
    }

    // ── Rate limit (#318) ────────────────────────────────────────────────────────

    private int RevealPermitLimit() =>
        factory.Services.GetRequiredService<IConfiguration>().GetValue("RateLimiting:AdminReveal:PermitLimit", 10);

    [Fact]
    public async Task Reveal_RateLimit_Returns429AfterTheConfiguredPermitCount()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));
        var client = factory.CreateAdminClient($"rate-limit-solo-{Guid.NewGuid():N}");
        var permitLimit = RevealPermitLimit();

        for (var i = 0; i < permitLimit; i++)
        {
            var response = await RevealAsync(client, "travelers", traveler.Id, nameof(Traveler.Email));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var rejected = await RevealAsync(client, "travelers", traveler.Id, nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    /// <summary>Partitioned by actor (oid), not by IP alone: two admins never share a bucket, so
    /// one exhausting their own limit never throttles the other.</summary>
    [Fact]
    public async Task Reveal_RateLimit_TwoDifferentActors_DoNotShareABucket()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));
        var actorA = factory.CreateAdminClient($"rate-limit-a-{Guid.NewGuid():N}");
        var actorB = factory.CreateAdminClient($"rate-limit-b-{Guid.NewGuid():N}");
        var permitLimit = RevealPermitLimit();

        for (var i = 0; i < permitLimit; i++)
        {
            var response = await RevealAsync(actorA, "travelers", traveler.Id, nameof(Traveler.Email));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var actorARejected = await RevealAsync(actorA, "travelers", traveler.Id, nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.TooManyRequests, actorARejected.StatusCode);

        // Actor B has never called this endpoint before in this test — a fresh bucket, unaffected
        // by actor A's exhausted one.
        var actorBResponse = await RevealAsync(actorB, "travelers", traveler.Id, nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.OK, actorBResponse.StatusCode);
    }
}
