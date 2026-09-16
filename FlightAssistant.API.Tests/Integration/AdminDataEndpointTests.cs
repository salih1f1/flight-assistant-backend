using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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

    /// <summary>
    /// #385/R4: the "has a value" sentinel is a string-column concept only — on a non-string
    /// column (<see cref="Traveler.Id"/>, a <see cref="Guid"/>) <c>*</c> is not special-cased at
    /// all, so it reaches the same value conversion any other filter value does and fails to parse,
    /// a 400 exactly like an unparseable value does today.
    /// </summary>
    [Fact]
    public async Task GetTableData_NonStringColumnFilteredOnStar_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync($"admin/data/travelers?filter={nameof(Traveler.Id)}:*");
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

    /// <summary>
    /// #319, the whole point of the issue: an audit row written through the real pipeline must
    /// carry a non-null <c>TraceId</c> equal to the request's own <c>X-Correlation-Id</c> response
    /// header — the same string a caller can already see and an operator can already find in the
    /// logs. This runs through <see cref="FlightAssistantWebApplicationFactory"/>'s real HTTP
    /// pipeline (<c>CorrelationIdMiddleware</c> → the <c>/admin</c> endpoint filter →
    /// <c>RevealValueCommandHandler</c>), and proves it despite the factory stripping
    /// <c>AdminAuditInterceptor</c> (see that class's own remarks) only because a reveal's audit
    /// row is written explicitly by the handler itself, never by the interceptor.
    /// </summary>
    [Fact]
    public async Task Reveal_Admin_SuccessfulReveal_AuditRowTraceId_MatchesTheResponsesCorrelationIdHeader()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await RevealAsync(factory.CreateAdminClient(), "travelers", traveler.Id, nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        var correlationId = Assert.Single(values);
        Assert.False(string.IsNullOrWhiteSpace(correlationId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var row = await db.AdminAuditLogs.SingleAsync(a => a.EntityId == traveler.Id.ToString());

        Assert.Equal(correlationId, row.TraceId);
    }

    /// <summary>
    /// #319: a caller-supplied <c>X-Correlation-Id</c> is not just echoed on the response — it is
    /// the exact string that ends up on the audit row, so a caller who already has the id (from an
    /// earlier response, or their own request tracing) can quote it and find the row it produced.
    /// </summary>
    [Fact]
    public async Task Reveal_Admin_ClientSuppliedCorrelationId_FlowsThroughToTheAuditRow()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var clientCorrelationId = $"client-supplied-{Guid.NewGuid():N}";
        var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", clientCorrelationId);

        var response = await RevealAsync(client, "travelers", traveler.Id, nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        Assert.Equal(clientCorrelationId, Assert.Single(values));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var row = await db.AdminAuditLogs.SingleAsync(a => a.EntityId == traveler.Id.ToString());

        Assert.Equal(clientCorrelationId, row.TraceId);
    }

    /// <summary>
    /// #318 final review, finding 2 (I2): a caller-supplied id that resolves the right row while
    /// spelled non-canonically (braced, uppercase) must still produce an audit row whose
    /// <c>EntityId</c> is the canonical form — <see cref="Guid.ToString()"/>'s default
    /// (lowercase, unbraced) — because that is exactly what <c>AdminAuditInterceptor.GetEntityId</c>
    /// writes for every other admin-write row on the same entity (it also calls
    /// <c>.ToString()</c> on the same underlying <see cref="Guid"/> value). Before this fix the
    /// handler stamped <c>request.Id</c> verbatim, so an auditor's
    /// <c>WHERE entity_id = '&lt;guid&gt;'</c> — the exact query the test above runs — would
    /// silently miss a reveal whose caller varied the id's spelling.
    /// </summary>
    [Fact]
    public async Task Reveal_Admin_BracedUppercaseId_AuditRowEntityId_IsCanonical()
    {
        var rawEmail = $"very-secret-{Marker()}@example.com";
        var traveler = Traveler.Register(rawEmail);
        await SeedAsync(db => db.Travelers.Add(traveler));

        var canonicalId = traveler.Id.ToString();
        var nonCanonicalId = $"{{{canonicalId.ToUpperInvariant()}}}";

        var response = await RevealAsync(factory.CreateAdminClient(), "travelers", nonCanonicalId, nameof(Traveler.Email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        // Deliberately the same lookup an auditor would run, and the same one
        // Reveal_Admin_SuccessfulReveal_WritesExactlyOneAuditRow... above uses: a plain equality
        // match on the canonical id, not a Contains/starts-with that could mask a formatting bug.
        var row = await db.AdminAuditLogs.SingleAsync(a => a.EntityId == canonicalId);

        Assert.Equal(AdminAuditAction.Reveal, row.Action);
        Assert.Contains(canonicalId, row.Summary);
        Assert.DoesNotContain(nonCanonicalId, row.Summary);
    }

    // ── Ordering, proven at the endpoint too (#318 final review, M6) ────────────

    /// <summary>
    /// Delegates every method to a real <see cref="AdminAuditRepository"/> except
    /// <see cref="SaveChangesAsync"/>, which always throws — the endpoint-level counterpart to
    /// <c>RevealValueCommandHandlerTests.Handle_WhenTheAuditSaveFails_PropagatesAndNeverReturnsAValue</c>.
    /// That handler-level test proves <c>Handle</c> throws; it stops one step short of proving the
    /// HTTP caller actually receives no value in the response body (#318 final review, M6) — this
    /// class is what lets a real end-to-end request exercise that failure without a real database
    /// failure to provoke it.
    /// </summary>
    private sealed class SaveFailingAdminAuditRepository(IAdminAuditRepository inner) : IAdminAuditRepository
    {
        public Task<(IReadOnlyList<AdminAuditLog> Items, int Total)> SearchAsync(
            int page, int pageSize, string? entityType, string? entityId, string? actorId,
            AdminAuditAction? action, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct = default) =>
            inner.SearchAsync(page, pageSize, entityType, entityId, actorId, action, fromUtc, toUtc, ct);

        public Task AddAsync(AdminAuditLog entry, CancellationToken ct = default) => inner.AddAsync(entry, ct);

        // Task.FromException, not a synchronous throw: a real Npgsql failure surfaces as a faulted
        // Task from inside an async method's own state machine, observable only once awaited (or
        // otherwise inspected) — not as an exception thrown on the calling thread before any Task
        // even exists. The difference matters here specifically: a synchronous throw would propagate
        // out of a "fire-and-forget" `_ = SaveChangesAsync(ct)` call before `.ContinueWith` could
        // even attach to it, so it would still (accidentally) fail the request under that mutation
        // and this test would pass for the wrong reason. A faulted Task instead behaves exactly like
        // NSubstitute's ThrowsAsync does in the handler-level test, so both tests are checking the
        // same failure shape.
        public Task SaveChangesAsync(CancellationToken ct = default) =>
            Task.FromException(new InvalidOperationException(
                "M6: simulated audit save failure for the endpoint-level ordering test."));
    }

    /// <summary>A standalone factory (not the shared "Integration" collection fixture, so this
    /// failure mode never leaks into any other test) that layers the throwing repository above onto
    /// <see cref="FlightAssistantWebApplicationFactory"/>'s ordinary InMemory/test-auth composition
    /// — mirrors <c>DbContextInterceptorWiringTests</c>' approach of building a bespoke factory for
    /// one specific wiring concern rather than restructuring the shared one.</summary>
    private sealed class SaveFailingWebApplicationFactory : FlightAssistantWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                var existing = services.Where(d => d.ServiceType == typeof(IAdminAuditRepository)).ToList();
                foreach (var d in existing) services.Remove(d);

                services.AddScoped<IAdminAuditRepository>(sp =>
                    new SaveFailingAdminAuditRepository(new AdminAuditRepository(sp.GetRequiredService<FlightAssistantDbContext>())));
            });
        }
    }

    /// <summary>
    /// #318 final review, M6: pushes the handler-level ordering assertion out to the HTTP
    /// response. A forced audit-save failure must surface as the ordinary unhandled-exception 500
    /// (<c>GlobalExceptionHandler</c>'s catch-all — <c>Title = "An unexpected error occurred."</c>,
    /// no <c>Detail</c>) whose body carries no <c>value</c> anywhere — not the raw revealed email,
    /// and not a <c>RevealedValueDto</c>-shaped success body under a different status code.
    /// </summary>
    [Fact]
    public async Task Reveal_Admin_WhenTheAuditSaveFails_Returns500_WithNoValueInTheBody()
    {
        using var failingFactory = new SaveFailingWebApplicationFactory();
        var rawEmail = $"very-secret-{Marker()}@example.com";
        var traveler = Traveler.Register(rawEmail);
        using (var scope = failingFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            db.Travelers.Add(traveler);
            await db.SaveChangesAsync();
        }

        var response = await RevealAsync(
            failingFactory.CreateAdminClient(), "travelers", traveler.Id, nameof(Traveler.Email));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rawEmail, body);
        Assert.DoesNotContain("\"value\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("traceId", body); // control: a real ProblemDetails body, not an empty one

        using var verifyScope = failingFactory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var auditRowCount = await verifyDb.AdminAuditLogs.CountAsync(a => a.EntityId == traveler.Id.ToString());
        Assert.Equal(0, auditRowCount);
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
