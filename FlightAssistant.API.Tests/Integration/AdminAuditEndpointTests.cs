using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/audit</c> hangs off the shared <c>/admin</c> group, so the auth cases here are
/// really checking that it was mapped onto that group rather than standalone.
///
/// <para>
/// Rows are seeded directly by constructing <see cref="AdminAuditLog"/> via its factory and adding
/// them to <see cref="FlightAssistantDbContext"/> — never by driving <c>AdminAuditInterceptor</c>
/// (Task 2's own tested surface), per the task brief, so these tests fail only for reasons that
/// belong to the read side.
/// </para>
///
/// <para>
/// The "Integration" collection shares a single <see cref="FlightAssistantWebApplicationFactory"/>
/// — and therefore a single InMemory database — across every test in every class in the collection.
/// Every test here therefore seeds with a fresh-per-test marker (folded into <c>EntityType</c> or
/// <c>ActorId</c>) and filters on it, rather than asserting anything about the table as a whole.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminAuditEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private async Task SeedAsync(params AdminAuditLog[] rows)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        db.AdminAuditLogs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static string Marker() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task GetAudit_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("admin/audit");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAudit_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var client = factory.CreateUserClient();

        var response = await client.GetAsync("admin/audit");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAudit_Admin_Returns200()
    {
        var marker = Marker();
        await SeedAsync(AdminAuditLog.Create(
            actorId: $"actor-{marker}", actorName: "Ada Admin", action: AdminAuditAction.Update,
            entityType: $"Type-{marker}", entityId: "1", summary: "Updated something"));

        var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"admin/audit?entityType=Type-{marker}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(result);
        var row = Assert.Single(result!.Items);
        Assert.Equal("Ada Admin", row.ActorName);
        Assert.Equal("Update", row.Action);
        Assert.Equal("Updated something", row.Summary);
    }

    [Fact]
    public async Task GetAudit_FilterByEntityTypeAndEntityId_NarrowsToTheHistoryOfThatRow()
    {
        var marker = Marker();
        await SeedAsync(
            AdminAuditLog.Create($"actor-{marker}", null, AdminAuditAction.Create, $"Type-{marker}", "42", "Created 42"),
            AdminAuditLog.Create($"actor-{marker}", null, AdminAuditAction.Update, $"Type-{marker}", "42", "Updated 42"),
            AdminAuditLog.Create($"actor-{marker}", null, AdminAuditAction.Update, $"Type-{marker}", "99", "Updated 99 — must not appear"));

        var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"admin/audit?entityType=Type-{marker}&entityId=42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(result);
        Assert.Equal(2, result!.Total);
        Assert.All(result.Items, r => Assert.Equal("42", r.EntityId));
    }

    [Fact]
    public async Task GetAudit_Filters_CombineWithAnd_NotOr()
    {
        var marker = Marker();
        var actorX = $"actorX-{marker}";
        var actorY = $"actorY-{marker}";
        var typeFoo = $"Foo-{marker}";
        var typeBar = $"Bar-{marker}";

        await SeedAsync(
            AdminAuditLog.Create(actorX, null, AdminAuditAction.Update, typeFoo, "1", "actorX/Foo — the only match"),
            AdminAuditLog.Create(actorY, null, AdminAuditAction.Update, typeFoo, "2", "actorY/Foo — wrong actor"),
            AdminAuditLog.Create(actorX, null, AdminAuditAction.Update, typeBar, "3", "actorX/Bar — wrong type"));

        var client = factory.CreateAdminClient();

        // If the filters were OR'd instead of AND'd, all three rows above would match.
        var response = await client.GetAsync($"admin/audit?entityType={typeFoo}&actorId={actorX}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(result);
        var row = Assert.Single(result!.Items);
        Assert.Equal("actorX/Foo — the only match", row.Summary);
    }

    [Fact]
    public async Task GetAudit_FilterByAction_NarrowsResults()
    {
        var marker = Marker();
        var actor = $"actor-{marker}";

        await SeedAsync(
            AdminAuditLog.Create(actor, null, AdminAuditAction.Create, $"Type-{marker}", "1", "created"),
            AdminAuditLog.Create(actor, null, AdminAuditAction.Delete, $"Type-{marker}", "1", "deleted"));

        var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"admin/audit?actorId={actor}&action=Delete");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(result);
        var row = Assert.Single(result!.Items);
        Assert.Equal("Delete", row.Action);
    }

    [Fact]
    public async Task GetAudit_DateRange_NarrowsResults()
    {
        var marker = Marker();
        var actor = $"actor-{marker}";
        var now = DateTime.UtcNow;

        await SeedAsync(
            AdminAuditLog.Create(actor, null, AdminAuditAction.Update, $"Type-{marker}", "1", "3 days ago — excluded", actionUtc: now.AddDays(-3)),
            AdminAuditLog.Create(actor, null, AdminAuditAction.Update, $"Type-{marker}", "2", "2 days ago — included", actionUtc: now.AddDays(-2)),
            AdminAuditLog.Create(actor, null, AdminAuditAction.Update, $"Type-{marker}", "3", "1 day ago — included", actionUtc: now.AddDays(-1)),
            AdminAuditLog.Create(actor, null, AdminAuditAction.Update, $"Type-{marker}", "4", "now — excluded", actionUtc: now));

        var client = factory.CreateAdminClient();

        var from = now.AddDays(-2.5);
        var to = now.AddDays(-0.5);
        var response = await client.GetAsync(
            $"admin/audit?actorId={actor}&fromUtc={Uri.EscapeDataString(from.ToString("O"))}&toUtc={Uri.EscapeDataString(to.ToString("O"))}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(result);
        Assert.Equal(2, result!.Total);
        Assert.All(result.Items, r => Assert.Contains("included", r.Summary));
    }

    [Fact]
    public async Task GetAudit_InvertedDateRange_Returns400WithAUsefulMessage()
    {
        var client = factory.CreateAdminClient();
        var now = DateTime.UtcNow;

        var response = await client.GetAsync(
            $"admin/audit?fromUtc={Uri.EscapeDataString(now.ToString("O"))}&toUtc={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FromUtc", body);
        Assert.Contains("ToUtc", body);
    }

    /// <summary>
    /// #319: the same correlation id a caller sees on <c>X-Correlation-Id</c> is also the
    /// <c>traceId</c> extension on a ProblemDetails error body — proven here via the endpoint's own
    /// validator-driven 400 (<c>GetAudit_InvertedDateRange_Returns400WithAUsefulMessage</c> above),
    /// so this is a real HTTP round trip through <c>CorrelationIdMiddleware</c> and
    /// <c>GlobalExceptionHandler</c> together, not two units tested in isolation.
    /// </summary>
    [Fact]
    public async Task GetAudit_InvertedDateRange_ProblemDetailsTraceId_MatchesTheResponsesCorrelationIdHeader()
    {
        var client = factory.CreateAdminClient();
        var now = DateTime.UtcNow;

        var response = await client.GetAsync(
            $"admin/audit?fromUtc={Uri.EscapeDataString(now.ToString("O"))}&toUtc={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        var correlationId = Assert.Single(values);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(correlationId, doc.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task GetAudit_Ordering_IsNewestFirst()
    {
        var marker = Marker();
        var actor = $"actor-{marker}";
        var now = DateTime.UtcNow;

        await SeedAsync(
            AdminAuditLog.Create(actor, null, AdminAuditAction.Update, $"Type-{marker}", "1", "oldest", actionUtc: now.AddMinutes(-30)),
            AdminAuditLog.Create(actor, null, AdminAuditAction.Update, $"Type-{marker}", "2", "newest", actionUtc: now.AddMinutes(-10)),
            AdminAuditLog.Create(actor, null, AdminAuditAction.Update, $"Type-{marker}", "3", "middle", actionUtc: now.AddMinutes(-20)));

        var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"admin/audit?actorId={actor}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(result);
        Assert.Equal(3, result!.Items.Count);
        Assert.Equal("newest", result.Items[0].Summary);
        Assert.Equal("middle", result.Items[1].Summary);
        Assert.Equal("oldest", result.Items[2].Summary);
    }

    [Fact]
    public async Task GetAudit_Total_ReflectsFilteredCount_NotTableCount()
    {
        // The classic bug in this shape: a Total computed before the filters are applied. Seed
        // rows the filter excludes (a distinct EntityType) alongside the ones it matches, and use a
        // PageSize smaller than the matching set so Items.Count and Total must legitimately differ.
        var marker = Marker();
        var matchingType = $"Match-{marker}";
        var excludedType = $"Excluded-{marker}";
        var actor = $"actor-{marker}";

        var matching = Enumerable.Range(1, 5)
            .Select(i => AdminAuditLog.Create(actor, null, AdminAuditAction.Update, matchingType, i.ToString(), $"match {i}"))
            .ToArray();
        var excluded = Enumerable.Range(1, 3)
            .Select(i => AdminAuditLog.Create(actor, null, AdminAuditAction.Update, excludedType, i.ToString(), $"excluded {i}"))
            .ToArray();
        await SeedAsync([.. matching, .. excluded]);

        var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"admin/audit?entityType={matchingType}&page=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(result);
        Assert.Equal(5, result!.Total);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task GetAudit_NullEntityId_Row_ReturnsCorrectly_AndIsNotSilentlyDroppedByTheEntityFilterPath()
    {
        // Amendment: EntityId is null for a create of a store-generated-key entity. Such a row must
        // still surface through EntityType-only filtering (no EntityId given), with a null EntityId
        // in the response — never dropped, never coerced into some sentinel value.
        var marker = Marker();
        var type = $"Type-{marker}";
        var afterJson = """{"id":123,"name":"new row"}""";

        await SeedAsync(AdminAuditLog.Create(
            actorId: $"actor-{marker}", actorName: null, action: AdminAuditAction.Create,
            entityType: type, entityId: null, summary: "Created a store-generated-key row",
            after: afterJson));

        var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"admin/audit?entityType={type}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(result);
        var row = Assert.Single(result!.Items);
        Assert.Null(row.EntityId);
        Assert.Equal("Created a store-generated-key row", row.Summary);

        // And an explicit EntityId filter alongside the same EntityType must never match it —
        // a null column is never equal to any supplied string.
        var narrowed = await client.GetAsync($"admin/audit?entityType={type}&entityId=123");
        var narrowedResult = await narrowed.Content.ReadFromJsonAsync<PagedResult<AdminAuditDto>>();
        Assert.NotNull(narrowedResult);
        Assert.Empty(narrowedResult!.Items);
    }

    [Fact]
    public async Task GetAudit_BeforeAndAfter_RenderAsRawJsonObjects_NotEscapedStrings()
    {
        var marker = Marker();
        var type = $"Type-{marker}";
        const string beforeJson = """{"status":"old"}""";
        const string afterJson = """{"status":"new"}""";

        await SeedAsync(AdminAuditLog.Create(
            actorId: $"actor-{marker}", actorName: null, action: AdminAuditAction.Update,
            entityType: type, entityId: "1", summary: "status changed",
            before: beforeJson, after: afterJson));

        var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"admin/audit?entityType={type}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        // Rendered as nested objects: the inner property appears as ordinary unescaped JSON...
        Assert.Contains("\"status\":\"old\"", body);
        Assert.Contains("\"status\":\"new\"", body);
        // ...never as a JSON-escaped string-in-a-string (which would show the quotes backslash-escaped).
        Assert.DoesNotContain("\\\"status\\\"", body);
    }

    [Fact]
    public async Task GetAudit_NullBeforeColumn_RendersAsJsonNull_NotOmittedOrAStringLiteral()
    {
        var marker = Marker();
        var type = $"Type-{marker}";

        // A create: Before is null by construction (see AdminAuditLog.Create's doc comment).
        await SeedAsync(AdminAuditLog.Create(
            actorId: $"actor-{marker}", actorName: null, action: AdminAuditAction.Create,
            entityType: type, entityId: "1", summary: "created", after: """{"a":1}"""));

        var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"admin/audit?entityType={type}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A single read of the raw body: HttpContent's underlying stream is not repeatable, so the
        // structured assertion below deserializes this same string rather than calling
        // ReadFromJsonAsync separately (which would read an already-consumed stream).
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"before\":null", body);

        var result = JsonSerializer.Deserialize<PagedResult<AdminAuditDto>>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        var row = Assert.Single(result!.Items);
        Assert.Null(row.Before);
    }
}
