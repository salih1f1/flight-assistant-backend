using System.Diagnostics;
using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Domain.DataSources.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Everything about <see cref="AdminAuditInterceptor"/> that doesn't need a real transaction or a
/// real <c>jsonb</c> column — action derivation, masking, the append-only guard, the actor gate,
/// and the synchronous save path — is exercised here against EF Core's InMemory provider, which is
/// fast and needs no Docker. The two things InMemory genuinely cannot see (same-transaction
/// rollback, and a real <c>jsonb</c> round-trip) are covered separately in
/// <see cref="AdminAuditInterceptorPostgresTests"/>.
/// </summary>
public class AdminAuditInterceptorTests
{
    private static FlightAssistantDbContext CreateContext(string dbName, AdminContext adminContext) =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new AdminAuditInterceptor(adminContext))
            .Options);

    private static AdminContext AdminActor(string actorId = "admin-oid-1", string? actorName = "Ada Admin") =>
        new() { ActorId = actorId, ActorName = actorName };

    // ── Types this interceptor never audits ─────────────────────────────────────
    // Stated in NotAuditedTypes and, until #469, pinned by nothing.

    /// <summary>
    /// #460: the health observer flushes these rows on a background timer, once per source per
    /// coalescing window. Auditing them would put a row in the admin audit log for every outbound
    /// HTTP call the application makes — the log an operator reads for "who changed what" would be
    /// mostly machine bookkeeping. An admin actor is set here precisely because that is the case
    /// that would otherwise write one: the flush can run inside a scope an admin request opened.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_DataSourceStatus_WritesNoAuditRow()
    {
        var dbName = Guid.NewGuid().ToString();

        using var db = CreateContext(dbName, AdminActor());
        db.DataSourceStatuses.Add(new DataSourceStatus { Key = "aeroapi", LastOutcome = "Ok" });
        await db.SaveChangesAsync();

        Assert.Empty(await db.AdminAuditLogs.ToListAsync());
    }

    /// <summary>
    /// An update is the shape the flush actually writes — one row per source, overwritten again and
    /// again — so the exclusion has to hold for a modification, not just an insert.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_UpdatedDataSourceStatus_WritesNoAuditRow()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.DataSourceStatuses.Add(new DataSourceStatus { Key = "aeroapi", LastOutcome = "Ok" });
            await seed.SaveChangesAsync();
        }

        using var db = CreateContext(dbName, AdminActor());
        var row = await db.DataSourceStatuses.SingleAsync(r => r.Key == "aeroapi");
        row.LastOutcome = "Failed";
        row.ConsecutiveFailures = 1;
        await db.SaveChangesAsync();

        Assert.Empty(await db.AdminAuditLogs.ToListAsync());
    }

    /// <summary>
    /// The queue table is its own history (#367, spec §4.5); auditing it would add an Update row per
    /// status change of every request, under the verification job's system actor.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_VerificationRequest_WritesNoAuditRow()
    {
        var dbName = Guid.NewGuid().ToString();

        using var db = CreateContext(dbName, AdminActor());
        db.VerificationRequests.Add(VerificationRequest.Create(
            VerificationEntityType.Airport, "42", ["ourairports"], VerificationTrigger.Admin,
            requestedBy: "admin-oid-1", estimatedCostUsd: 0m,
            requestedUtc: new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        Assert.Empty(await db.AdminAuditLogs.ToListAsync());
    }

    // ── No actor ⇒ no row ───────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_NoActor_WritesNoAuditRow()
    {
        var dbName = Guid.NewGuid().ToString();
        var adminContext = new AdminContext(); // ActorId unset — an ordinary traveler-path save

        using var db = CreateContext(dbName, adminContext);
        db.Travelers.Add(Traveler.Register("traveler@example.com"));
        await db.SaveChangesAsync();

        Assert.Empty(await db.AdminAuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_BlankActor_WritesNoAuditRow()
    {
        var dbName = Guid.NewGuid().ToString();
        var adminContext = new AdminContext { ActorId = "   " }; // blank, not null

        using var db = CreateContext(dbName, adminContext);
        db.Travelers.Add(Traveler.Register("traveler2@example.com"));
        await db.SaveChangesAsync();

        Assert.Empty(await db.AdminAuditLogs.ToListAsync());
    }

    // ── Action derivation: Create / Update / Delete ────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_AddedEntity_DerivesCreateAction()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateContext(dbName, AdminActor());

        db.Countries.Add(Country.Create("FR", "France"));
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();
        Assert.Equal(AdminAuditAction.Create, row.Action);
        Assert.Equal(nameof(Country), row.EntityType);
        Assert.Null(row.Before);
        Assert.NotNull(row.After);
    }

    [Fact]
    public async Task SaveChangesAsync_ModifiedEntity_DerivesUpdateAction()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.Countries.Add(Country.Create("DE", "Germany"));
            await seed.SaveChangesAsync();
        }

        using (var db = CreateContext(dbName, AdminActor()))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "DE");
            country.Update("Deutschland", null, null, null, null, null, null, null);
            await db.SaveChangesAsync();
        }

        using (var verify = CreateContext(dbName, AdminActor()))
        {
            var row = await verify.AdminAuditLogs.SingleAsync(r => r.Action == AdminAuditAction.Update);
            Assert.Equal(nameof(Country), row.EntityType);
            Assert.NotNull(row.Before);
            Assert.NotNull(row.After);
            Assert.Contains("Deutschland", row.After);
            Assert.DoesNotContain("Deutschland", row.Before);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_DeletedEntity_DerivesDeleteAction()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.Countries.Add(Country.Create("IT", "Italy"));
            await seed.SaveChangesAsync();
        }

        using (var db = CreateContext(dbName, AdminActor()))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "IT");
            db.Countries.Remove(country);
            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var row = await verify.AdminAuditLogs.SingleAsync(r => r.Action == AdminAuditAction.Delete);
        Assert.Equal(nameof(Country), row.EntityType);
        Assert.NotNull(row.Before);
        Assert.Null(row.After);
        Assert.Contains("IT", row.Before);
    }

    // ── Unchanged / Detached are ignored ────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_NoChanges_WritesNoAdditionalRow()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.Countries.Add(Country.Create("ES", "Spain"));
            await seed.SaveChangesAsync(); // one Create row
        }

        using (var db = CreateContext(dbName, AdminActor()))
        {
            _ = await db.Countries.SingleAsync(c => c.Code == "ES"); // tracked, Unchanged
            await db.SaveChangesAsync(); // nothing modified — should add nothing
        }

        using var verify = CreateContext(dbName, AdminActor());
        Assert.Single(await verify.AdminAuditLogs.ToListAsync());
    }

    // ── Verify detection: modified properties non-empty and a subset of IVerifiable ───

    [Fact]
    public async Task SaveChangesAsync_ModifiedVerificationSubset_DerivesVerifyAction()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.Countries.Add(Country.Create("PT", "Portugal"));
            await seed.SaveChangesAsync();
        }

        using (var db = CreateContext(dbName, AdminActor()))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "PT");
            // Touches IsVerified/VerifiedAt/VerifiedBy/VerificationSource; VerificationIssue stays
            // null -> null (already null on an unverified row) so EF never marks it modified — the
            // modified set is a proper (non-empty) subset of IVerifiable, not an exact match.
            country.Verify("admin-oid-1", VerificationSources.Manual);
            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var row = await verify.AdminAuditLogs.SingleAsync(r => r.Action == AdminAuditAction.Verify);
        Assert.Equal(nameof(Country), row.EntityType);
    }

    [Fact]
    public async Task SaveChangesAsync_VerificationPlusAnotherProperty_DerivesUpdateAction()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.Countries.Add(Country.Create("NL", "Netherlands"));
            await seed.SaveChangesAsync();
        }

        using (var db = CreateContext(dbName, AdminActor()))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "NL");
            country.Verify("admin-oid-1", VerificationSources.Manual);
            country.Update("The Netherlands", null, null, null, null, null, null, null); // renaming too
            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var row = await verify.AdminAuditLogs.SingleAsync(r => r.EntityType == nameof(Country) && r.Action != AdminAuditAction.Create);
        Assert.Equal(AdminAuditAction.Update, row.Action);
    }

    [Fact]
    public async Task SaveChangesAsync_UnverifyOnVerifiedRow_DerivesVerifyAction()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            var country = Country.Create("UY", "Test Country UY");
            country.Verify("admin-oid-1", VerificationSources.Manual);
            seed.Countries.Add(country);
            await seed.SaveChangesAsync();
        }

        using (var db = CreateContext(dbName, AdminActor()))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "UY");
            country.Unverify(); // IsVerified/VerifiedAt/VerifiedBy/VerificationSource all change
            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var row = await verify.AdminAuditLogs.SingleAsync(r => r.EntityType == nameof(Country) && r.Action != AdminAuditAction.Create);
        Assert.Equal(AdminAuditAction.Verify, row.Action);
    }

    [Fact]
    public async Task SaveChangesAsync_MarkVerificationFailedOnVerifiedRow_DerivesVerifyAction()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            var country = Country.Create("UZ", "Test Country UZ");
            country.Verify("admin-oid-1", VerificationSources.Manual);
            seed.Countries.Add(country);
            await seed.SaveChangesAsync();
        }

        using (var db = CreateContext(dbName, AdminActor()))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "UZ");
            // Touches all five IVerifiable properties, including VerificationIssue this time
            // (null -> "mismatch"), still a subset of the IVerifiable set.
            country.MarkVerificationFailed("Name differs (ourairports)");
            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var row = await verify.AdminAuditLogs.SingleAsync(r => r.EntityType == nameof(Country) && r.Action != AdminAuditAction.Create);
        Assert.Equal(AdminAuditAction.Verify, row.Action);
    }

    // ── Guard: the subset rule must not turn an ordinary update into a Verify ─

    [Fact]
    public async Task SaveChangesAsync_ModifiedOnlyNonVerificationProperty_DerivesUpdateAction()
    {
        // Distinguishes the subset rule from a buggy "non-empty implies Verify" rule: this save's
        // modified-property set (just Name) is disjoint from IVerifiable, not a subset of it, and
        // must still resolve to Update.
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.Countries.Add(Country.Create("VA", "Test Country VA"));
            await seed.SaveChangesAsync();
        }

        using (var db = CreateContext(dbName, AdminActor()))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "VA");
            country.Update("Renamed Country VA", null, null, null, null, null, null, null);
            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var row = await verify.AdminAuditLogs.SingleAsync(r => r.EntityType == nameof(Country) && r.Action != AdminAuditAction.Create);
        Assert.Equal(AdminAuditAction.Update, row.Action);
    }

    // ── ActionOverride wins over derivation (the CSV import case) ─────────────

    [Fact]
    public async Task SaveChangesAsync_ActionOverrideSet_WinsOverAddedDerivation()
    {
        var dbName = Guid.NewGuid().ToString();
        var adminContext = AdminActor();
        adminContext.ActionOverride = AdminAuditAction.Import;

        using var db = CreateContext(dbName, adminContext);
        db.Countries.Add(Country.Create("BE", "Belgium"));
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();
        Assert.Equal(AdminAuditAction.Import, row.Action);
    }

    [Fact]
    public async Task SaveChangesAsync_ActionOverrideSet_WinsOverModifiedDerivation()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.Countries.Add(Country.Create("SE", "Sweden"));
            await seed.SaveChangesAsync();
        }

        var adminContext = AdminActor();
        adminContext.ActionOverride = AdminAuditAction.Import;
        using (var db = CreateContext(dbName, adminContext))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "SE");
            country.Update("Kingdom of Sweden", null, null, null, null, null, null, null);
            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var row = await verify.AdminAuditLogs.SingleAsync(r => r.Action == AdminAuditAction.Import);
        Assert.Equal(nameof(Country), row.EntityType);
    }

    // ── Masking: raw sensitive values appear nowhere in the persisted rows ────

    [Fact]
    public async Task SaveChangesAsync_MasksSensitiveProperties_RawValuesAppearNowhereInAnyRow()
    {
        const string rawEmail = "very-secret-traveler@example.com";
        const string rawToken = "device-token-super-secret-abc123";
        const string rawAccountNumber = "ACCT-SECRET-999";

        var dbName = Guid.NewGuid().ToString();
        using (var db = CreateContext(dbName, AdminActor()))
        {
            var traveler = Traveler.Register(rawEmail, "Some Traveler");
            traveler.AddDeviceToken("ios", rawToken);
            db.Travelers.Add(traveler);
            db.TravelerLoyaltyMemberships.Add(
                TravelerLoyaltyMembership.Create(traveler.Id, 1, rawAccountNumber));

            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var rows = await verify.AdminAuditLogs.ToListAsync();
        Assert.NotEmpty(rows);

        // Search the WHOLE serialized row — every column, not just the one expected to carry it —
        // so a masking gap in an unexpected field (e.g. Summary) can't hide behind a narrower assertion.
        foreach (var row in rows)
        {
            var wholeRow = JsonSerializer.Serialize(new
            {
                row.ActorId,
                row.ActorName,
                row.EntityType,
                row.EntityId,
                row.Summary,
                row.Before,
                row.After,
                row.TraceId
            });

            Assert.DoesNotContain(rawEmail, wholeRow, StringComparison.Ordinal);
            Assert.DoesNotContain(rawToken, wholeRow, StringComparison.Ordinal);
            Assert.DoesNotContain(rawAccountNumber, wholeRow, StringComparison.Ordinal);
        }

        // And the masking actually fired (not just "didn't crash") — parsed as JSON, not a raw
        // substring search: System.Text.Json's default encoder escapes '<'/'>' (e.g. <), so
        // the literal text "<redacted>" never appears verbatim in the serialized string even
        // though the JSON value it decodes to is exactly that.
        var travelerRow = rows.Single(r => r.EntityType == nameof(Traveler));
        using var afterDoc = JsonDocument.Parse(travelerRow.After!);
        Assert.Equal("<redacted>", afterDoc.RootElement.GetProperty(nameof(Traveler.Email)).GetString());
    }

    // ── The synchronous SaveChanges() path is supported, not silently skipped ─

    [Fact]
    public void SaveChanges_Sync_AlsoWritesAuditRow()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateContext(dbName, AdminActor());

        db.Countries.Add(Country.Create("NO", "Norway"));
        db.SaveChanges(); // synchronous overload

        var row = db.AdminAuditLogs.Single();
        Assert.Equal(AdminAuditAction.Create, row.Action);
    }

    // ── Append-only: nothing may edit or remove an audit row ──────────────────

    [Fact]
    public async Task SaveChangesAsync_ModifyingAnAuditRow_Throws()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateContext(dbName, AdminActor());

        db.Countries.Add(Country.Create("GR", "Greece"));
        await db.SaveChangesAsync(); // creates one AdminAuditLog row as a side effect

        var auditRow = await db.AdminAuditLogs.SingleAsync();
        db.Entry(auditRow).Property(nameof(AdminAuditLog.Summary)).CurrentValue = "tampered";
        db.Entry(auditRow).State = EntityState.Modified;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_DeletingAnAuditRow_Throws()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateContext(dbName, AdminActor());

        db.Countries.Add(Country.Create("PL", "Poland"));
        await db.SaveChangesAsync();

        var auditRow = await db.AdminAuditLogs.SingleAsync();
        db.AdminAuditLogs.Remove(auditRow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    // The append-only guard must fire regardless of who — or whether anyone — is saving. It used
    // to sit below the actor gate, so a no-actor save (every ordinary traveler write, every
    // hosted service, every future background job) could silently tamper with an audit row; the
    // two tests above only exercised the guard through an actor-bearing context and so never
    // caught that (#313 Task 2 review, F1/I1).
    [Fact]
    public async Task SaveChangesAsync_DeletingAnAuditRow_ThrowsEvenWithoutAnActor()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = CreateContext(dbName, AdminActor()))
        {
            seed.Countries.Add(Country.Create("TR", "Turkey"));
            await seed.SaveChangesAsync(); // creates one AdminAuditLog row as a side effect
        }

        using var db = CreateContext(dbName, new AdminContext()); // no actor set
        var auditRow = await db.AdminAuditLogs.SingleAsync();
        db.AdminAuditLogs.Remove(auditRow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    // ── EntityId comes from EF primary-key metadata ────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_EntityId_UsesPrimaryKeyMetadata_NotAnyOtherProperty()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateContext(dbName, AdminActor());

        var traveler = Traveler.Register(Guid.NewGuid(), "pk-test@example.com");
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();
        Assert.Equal(traveler.Id.ToString(), row.EntityId);
    }

    [Fact]
    public async Task SaveChangesAsync_CompositeKey_JoinsPartsWithPipe()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateContext(dbName, AdminActor());

        db.Set<SeedVersion>().Add(new SeedVersion { Dataset = "airports", Version = 3, AppliedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();
        Assert.Equal("airports|3", row.EntityId);
    }

    // ── Trace correlation (#319): IAdminContext.CorrelationId is primary, Activity.Current is a
    //    secondary fallback — never the reverse. Nothing in this file exercised TraceId at all
    //    before #319; these are the first tests on this interceptor's TraceId resolution.

    [Fact]
    public async Task SaveChangesAsync_ActorWithCorrelationId_AuditRowTraceIdMatchesIt()
    {
        var dbName = Guid.NewGuid().ToString();
        var adminContext = new AdminContext
        {
            ActorId = "admin-oid-1",
            ActorName = "Ada Admin",
            CorrelationId = "corr-abc-123"
        };

        using var db = CreateContext(dbName, adminContext);
        db.Countries.Add(Country.Create("TC", "Trace Country"));
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();
        Assert.Equal("corr-abc-123", row.TraceId);
    }

    /// <summary>
    /// Mutation-provable precedence check: with both a populated <c>CorrelationId</c> and an
    /// ambient <see cref="Activity"/> carrying a *different* id, the row must carry the
    /// <c>CorrelationId</c> — swapping the interceptor's <c>_adminContext.CorrelationId ??
    /// Activity.Current?.Id</c> to the reverse order would make this fail while the pairing tests
    /// below (each with only one source populated) would keep passing either way.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_CorrelationIdAndAmbientActivityBothPresent_TraceIdPrefersTheCorrelationId()
    {
        using var activity = new Activity("interceptor-trace-test").Start();
        var dbName = Guid.NewGuid().ToString();
        var adminContext = new AdminContext
        {
            ActorId = "admin-oid-1",
            ActorName = "Ada Admin",
            CorrelationId = "corr-wins-over-activity"
        };

        using var db = CreateContext(dbName, adminContext);
        db.Countries.Add(Country.Create("TP", "Trace Precedence"));
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();
        Assert.Equal("corr-wins-over-activity", row.TraceId);
        Assert.NotEqual(activity.Id, row.TraceId);
    }

    [Fact]
    public async Task SaveChangesAsync_NoCorrelationIdButAmbientActivity_FallsBackToTheActivityId()
    {
        using var activity = new Activity("interceptor-trace-fallback-test").Start();
        var dbName = Guid.NewGuid().ToString();
        var adminContext = AdminActor(); // no CorrelationId set

        using var db = CreateContext(dbName, adminContext);
        db.Countries.Add(Country.Create("TF", "Trace Fallback"));
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();
        Assert.Equal(activity.Id, row.TraceId);
    }

    [Fact]
    public async Task SaveChangesAsync_NeitherCorrelationIdNorActivity_TraceIdIsNull()
    {
        Assert.Null(Activity.Current);
        var dbName = Guid.NewGuid().ToString();
        var adminContext = AdminActor(); // no CorrelationId set

        using var db = CreateContext(dbName, adminContext);
        db.Countries.Add(Country.Create("TN", "Trace Null"));
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();
        Assert.Null(row.TraceId);
    }

    // ── entry.Metadata.ClrType, not entry.Entity.GetType(), is what BuildAuditLog must key on ──
    //
    // Swapping BuildAuditLog's `entry.Metadata.ClrType` for `entry.Entity.GetType()` currently
    // breaks zero shipped tests: nothing in the real model has a proxy or a mapped subclass, so
    // the two are always identical there (#313 Task 2 review, F4). This model proves the
    // divergence exists and that only Metadata.ClrType survives it, without pulling in the
    // Microsoft.EntityFrameworkCore.Proxies package: EF's own entity-type resolution falls back
    // from an unmapped runtime type to the nearest *mapped* ancestor — the exact mechanism that
    // lets a lazy-loading proxy subclass be added through a DbSet<TBase> with no extra
    // configuration. Adding a WidgetSubclass instance (never itself mapped) through
    // DbSet<Widget> therefore produces an EntityEntry whose Metadata.ClrType is Widget but whose
    // Entity.GetType() is WidgetSubclass.

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class WidgetSubclass : Widget
    {
    }

    private sealed class ClrTypeProbeDbContext(DbContextOptions<ClrTypeProbeDbContext> options)
        : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
        public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // WidgetSubclass is deliberately never mentioned here — that's what makes it unmapped.
            modelBuilder.Entity<Widget>();
            modelBuilder.Entity<AdminAuditLog>();
        }
    }

    [Fact]
    public async Task SaveChangesAsync_AddedEntityIsAnUnmappedSubclassOfAMappedType_RecordsTheMappedTypeName()
    {
        var options = new DbContextOptionsBuilder<ClrTypeProbeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new AdminAuditInterceptor(AdminActor()))
            .Options;

        using var db = new ClrTypeProbeDbContext(options);
        db.Widgets.Add(new WidgetSubclass { Name = "gadget" }); // runtime type WidgetSubclass, mapped type Widget
        await db.SaveChangesAsync();

        var row = await db.AdminAuditLogs.SingleAsync();

        // entry.Metadata.ClrType ("Widget", the mapped type) is what must be recorded. Swapping
        // BuildAuditLog to entry.Entity.GetType() would record "WidgetSubclass" here instead and
        // fails this assertion — proving the pin matters, unlike the untested claim before this.
        Assert.Equal(nameof(Widget), row.EntityType);
    }
}
