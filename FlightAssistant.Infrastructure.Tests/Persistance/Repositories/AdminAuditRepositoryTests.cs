using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <see cref="AdminAuditRepository.SearchAsync"/> against real PostgreSQL (#313 final review, I2).
///
/// <para>
/// <b>Why this class exists at all.</b> Before this, the only tests exercising the read side
/// (<c>AdminAuditEndpointTests</c>) ran on <c>UseInMemoryDatabase</c>, which ignores
/// <see cref="DateTimeKind"/> entirely and translates every <c>Where</c> clause client-side rather
/// than to SQL. That blindness is exactly what let C1 ship: a bare-date query string
/// (<c>?fromUtc=2026-09-01</c>, no <c>Z</c>/offset) binds as <see cref="DateTimeKind.Unspecified"/>,
/// and Npgsql throws <see cref="ArgumentException"/> writing that against the <c>timestamptz</c>
/// <c>ActionUtc</c> column — a 500 in every deployed environment, invisible to the InMemory suite.
/// Fixed at the API boundary (<c>AdminAuditEndpoints</c> now binds <c>DateTimeOffset?</c> and
/// projects through <c>.UtcDateTime</c>), but this class is what makes that regression, or any
/// future variant of it, actually detectable — closing the blindness, not just the one bug it let
/// through.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminAuditRepositoryTests : PostgresTestBase
{
    public AdminAuditRepositoryTests(PostgresContainerFixture fixture) : base(fixture) { }

    private static AdminAuditRepository Repository(FlightAssistantDbContext db) => new(db);

    /// <summary>
    /// The C1 regression itself, reproduced one layer down from the endpoint: a
    /// <see cref="DateTimeKind.Unspecified"/> <c>fromUtc</c> — exactly what
    /// <c>DateTime? fromUtc</c> model binding used to hand the repository for a bare-date query
    /// string — must not throw against real Postgres, and must still filter correctly.
    /// </summary>
    [Fact]
    public async Task SearchAsync_FromUtcWithUnspecifiedKind_DoesNotThrow_AndStillFilters()
    {
        await using var db = CreateDbContext();
        db.AdminAuditLogs.AddRange(
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Create, "Traveler", "1", "before the window",
                actionUtc: new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc)),
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Create, "Traveler", "2", "inside the window",
                actionUtc: new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        // Kind is deliberately Unspecified — not converted to Utc — reproducing the exact shape
        // that reached AdminAuditRepository before AdminAuditEndpoints was fixed to bind
        // DateTimeOffset? and project through .UtcDateTime. AdminAuditRepository.ToUtcKind
        // normalises this defensively, so it should never throw regardless of caller behaviour.
        var fromUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified);

        (IReadOnlyList<AdminAuditLog> Items, int Total) result = (Array.Empty<AdminAuditLog>(), -1);
        var exception = await Record.ExceptionAsync(async () =>
            result = await Repository(db).SearchAsync(1, 50, null, null, null, null, fromUtc, null));

        Assert.Null(exception);
        Assert.Equal(1, result.Total);
        Assert.Equal("inside the window", Assert.Single(result.Items).Summary);
    }

    [Fact]
    public async Task SearchAsync_FilterByEntityType_NarrowsAgainstRealPostgres()
    {
        await using var db = CreateDbContext();
        db.AdminAuditLogs.AddRange(
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", "1", "matches"),
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "DeviceToken", "2", "wrong type"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).SearchAsync(1, 50, "Traveler", null, null, null, null, null);

        Assert.Equal(1, total);
        Assert.Equal("matches", Assert.Single(items).Summary);
    }

    [Fact]
    public async Task SearchAsync_FilterByEntityId_NarrowsAgainstRealPostgres()
    {
        await using var db = CreateDbContext();
        db.AdminAuditLogs.AddRange(
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", "42", "matches"),
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", "99", "wrong id"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).SearchAsync(1, 50, null, "42", null, null, null, null);

        Assert.Equal(1, total);
        Assert.Equal("matches", Assert.Single(items).Summary);
    }

    /// <summary>M3: an <c>oid</c> claim's casing varies by source, so <c>ActorId</c> matches
    /// case-insensitively against real Postgres (a plain <c>==</c> would translate to a
    /// case-sensitive <c>text</c> comparison and silently miss this).</summary>
    [Fact]
    public async Task SearchAsync_FilterByActorId_IsCaseInsensitive()
    {
        await using var db = CreateDbContext();
        db.AdminAuditLogs.Add(
            AdminAuditLog.Create("Actor-ABC-123", null, AdminAuditAction.Update, "Traveler", "1", "matches"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).SearchAsync(1, 50, null, null, "actor-abc-123", null, null, null);

        Assert.Equal(1, total);
        Assert.Equal("matches", Assert.Single(items).Summary);
    }

    /// <summary>
    /// Documents the deliberate asymmetry with <c>ActorId</c> (#313 final review, M3 ruling):
    /// <c>EntityType</c> is a CLR type name — generated, not typed by a human off some other
    /// source — so it stays an exact, case-sensitive match. A case-mismatched value legitimately
    /// finds nothing.
    /// </summary>
    [Fact]
    public async Task SearchAsync_FilterByEntityType_IsCaseSensitive()
    {
        await using var db = CreateDbContext();
        db.AdminAuditLogs.Add(
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", "1", "would not match lowercase"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).SearchAsync(1, 50, "traveler", null, null, null, null, null);

        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task SearchAsync_FilterByAction_NarrowsAgainstRealPostgres()
    {
        await using var db = CreateDbContext();
        db.AdminAuditLogs.AddRange(
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Delete, "Traveler", "1", "matches"),
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Create, "Traveler", "2", "wrong action"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).SearchAsync(1, 50, null, null, null, AdminAuditAction.Delete, null, null);

        Assert.Equal(1, total);
        Assert.Equal("matches", Assert.Single(items).Summary);
    }

    /// <summary>Both bounds are inclusive (Task 3's own design, reaffirmed by the final review's
    /// M4 ruling) — proven here against a real <c>timestamptz</c> comparison, not the InMemory
    /// provider's client-side evaluation.</summary>
    [Fact]
    public async Task SearchAsync_FromUtcAndToUtc_AreBothInclusive_AgainstRealPostgres()
    {
        await using var db = CreateDbContext();
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

        db.AdminAuditLogs.AddRange(
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", "1", "before range", actionUtc: from.AddSeconds(-1)),
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", "2", "exactly at from — included", actionUtc: from),
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", "3", "exactly at to — included", actionUtc: to),
            AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", "4", "after range", actionUtc: to.AddSeconds(1)));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).SearchAsync(1, 50, null, null, null, null, from, to);

        Assert.Equal(2, total);
        Assert.All(items, i => Assert.Contains("included", i.Summary));
    }

    /// <summary>The classic bug in this shape: a <c>Total</c> computed against the unfiltered
    /// table rather than after every <c>Where</c>. A <c>pageSize</c> smaller than the matching set
    /// forces <c>Total</c> and <c>Items.Count</c> to legitimately differ, so this only passes if
    /// the count is genuinely post-filter.</summary>
    [Fact]
    public async Task SearchAsync_Total_IsTheFilteredCount_NotTheTableCount()
    {
        await using var db = CreateDbContext();
        var matching = Enumerable.Range(1, 5)
            .Select(i => AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "Traveler", i.ToString(), $"match {i}"));
        var excluded = Enumerable.Range(1, 3)
            .Select(i => AdminAuditLog.Create("actor-1", null, AdminAuditAction.Update, "DeviceToken", i.ToString(), $"excluded {i}"));
        db.AdminAuditLogs.AddRange(matching.Concat(excluded));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).SearchAsync(1, 2, "Traveler", null, null, null, null, null);

        Assert.Equal(5, total);
        Assert.Equal(2, items.Count);
    }

    /// <summary>M5: rows written in the same admin save share one <c>ActionUtc</c> instant (see
    /// <c>AdminAuditInterceptor.WriteAuditRows</c>), so paging needs a secondary sort key to stay
    /// deterministic over that tie. Seeds a tie of 6 rows on one <c>ActionUtc</c>, pages through
    /// with <c>pageSize=2</c>, and asserts the three pages are disjoint and exhaustive — the
    /// property <c>Skip</c>/<c>Take</c> over an unordered tie group does not guarantee.</summary>
    [Fact]
    public async Task SearchAsync_RowsSharingOneActionUtc_PageDeterministically_ViaIdTiebreaker()
    {
        await using var db = CreateDbContext();
        var tiedInstant = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        db.AdminAuditLogs.AddRange(Enumerable.Range(1, 6)
            .Select(i => AdminAuditLog.Create("actor-1", null, AdminAuditAction.Import, "Traveler", i.ToString(), $"row {i}", actionUtc: tiedInstant)));
        await db.SaveChangesAsync();

        var seen = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var (items, total) = await Repository(db).SearchAsync(page, 2, null, null, null, null, null, null);
            Assert.Equal(6, total);
            seen.AddRange(items.Select(i => i.Id));
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    // ── AddAsync / SaveChangesAsync (#318 — the audited reveal's write half) ────

    /// <summary>
    /// <c>AddAsync</c> only stages the row — matches every other repository's
    /// Add-then-explicit-<c>SaveChangesAsync</c> split in this codebase. Proven against real
    /// Postgres: nothing committed until <c>SaveChangesAsync</c> is called on this same context.
    /// </summary>
    [Fact]
    public async Task AddAsync_DoesNotPersistUntilSaveChangesAsyncIsCalled()
    {
        await using var db = CreateDbContext();
        var entry = AdminAuditLog.Create("actor-1", "Ada Admin", AdminAuditAction.Reveal, "Traveler", "1", "Revealed Email on Traveler 1");

        await Repository(db).AddAsync(entry);

        await using var verifyBeforeSave = CreateDbContext();
        Assert.Empty(await verifyBeforeSave.AdminAuditLogs.ToListAsync());

        await Repository(db).SaveChangesAsync();

        await using var verifyAfterSave = CreateDbContext();
        var persisted = await verifyAfterSave.AdminAuditLogs.SingleAsync();
        Assert.Equal(AdminAuditAction.Reveal, persisted.Action);
        Assert.Equal("Revealed Email on Traveler 1", persisted.Summary);
    }

    /// <summary>
    /// <c>Before</c>/<c>After</c> are mapped <c>jsonb</c> (see <c>AdminAuditLogConfiguration</c>);
    /// a reveal row leaves both null (#318 — writing the value into either would defeat the whole
    /// point of an audited reveal). Proven as real Postgres <c>NULL</c>, not just an in-memory
    /// null reference, since InMemory has no <c>jsonb</c> column type to get this wrong against.
    /// </summary>
    [Fact]
    public async Task AddAsync_ThenSaveChangesAsync_PersistsNullBeforeAndAfter_AsRealPostgresNulls()
    {
        await using var db = CreateDbContext();
        var entry = AdminAuditLog.Create(
            "actor-1", "Ada Admin", AdminAuditAction.Reveal, "Traveler", "1", "Revealed Email on Traveler 1",
            before: null, after: null);

        await Repository(db).AddAsync(entry);
        await Repository(db).SaveChangesAsync();

        await using var verify = CreateDbContext();
        var persisted = await verify.AdminAuditLogs.SingleAsync();
        Assert.Null(persisted.Before);
        Assert.Null(persisted.After);
    }
}
