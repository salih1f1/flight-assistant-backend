using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
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

    // ── Verify detection: exactly the IVerifiable trio, and not a superset ────

    [Fact]
    public async Task SaveChangesAsync_ModifiedExactlyVerifiableTrio_DerivesVerifyAction()
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
            country.Verify("admin-oid-1"); // touches only IsVerified/VerifiedAt/VerifiedBy
            await db.SaveChangesAsync();
        }

        using var verify = CreateContext(dbName, AdminActor());
        var row = await verify.AdminAuditLogs.SingleAsync(r => r.Action == AdminAuditAction.Verify);
        Assert.Equal(nameof(Country), row.EntityType);
    }

    [Fact]
    public async Task SaveChangesAsync_ModifiedTrioPlusAnotherProperty_DerivesUpdateAction()
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
            country.Verify("admin-oid-1");
            country.Update("The Netherlands", null, null, null, null, null, null, null); // renaming too
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
}
