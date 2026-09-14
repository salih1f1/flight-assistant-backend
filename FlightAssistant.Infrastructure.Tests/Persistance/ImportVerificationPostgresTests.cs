using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// The one thing about CSV-import verification (#362) InMemory cannot see: whether a
/// <c>VerifiedAt</c> value actually reaches Postgres and comes back <see cref="DateTimeKind.Utc"/>
/// through a real <c>timestamptz</c> column, read through a fresh <see cref="FlightAssistantDbContext"/>
/// rather than the tracked entity that wrote it. Npgsql refuses a <see cref="DateTimeKind.Unspecified"/>
/// value outright — the exact bug class CLAUDE.md documents for date query parameters — so a
/// caller that handed a non-UTC <c>DateTime</c> to <c>ImportVerification.Resolve</c> without that
/// helper's own normalisation (#362 fix round 1) would 500 here, at <c>SaveChangesAsync</c>,
/// outside every handler's per-row <c>try</c>; InMemory accepts any <c>Kind</c> without complaint
/// and would never catch it.
///
/// <para>
/// The DTOs here are built by hand, deliberately bypassing <c>CsvImportParser</c> — this project
/// does not (and, per fix round 1, should not) reference <c>FlightAssistant.API</c>. That parser's
/// own <see cref="DateTimeKind.Utc"/> guarantee is proved in <c>CsvImportParserTests</c>
/// (API.Tests) instead; what this test proves is a different, non-CSV risk: any other caller of
/// the 8 import commands — <c>ImportVerification.Resolve</c> is public, not parser-only — handing
/// a <see cref="DateTimeKind.Unspecified"/> or <see cref="DateTimeKind.Local"/> value must still
/// round-trip through the real handler, the real repository, and a real Postgres connection
/// without crashing or losing the instant.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ImportVerificationPostgresTests : PostgresTestBase
{
    public ImportVerificationPostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Handle_ImportedRowCarryingUnspecifiedKindVerifiedAt_RoundTripsAsUtcThroughAFreshContext()
    {
        // No zone to convert from — ImportVerification reinterprets Unspecified as UTC (same
        // clock reading, Kind corrected), so the instant read back must match this value exactly,
        // just with Kind == Utc instead of Unspecified.
        var verifiedAtUnspecified = new DateTime(2026, 9, 14, 20, 31, 12, 123, DateTimeKind.Unspecified);

        await using (var db = CreateDbContext())
        {
            var handler = new ImportAlliancesCommandHandler(
                new AllianceRepository(db), new AdminContext { ActorId = "importing-admin" });
            var dto = new AllianceImportDto("ZU", "Zeta Unspecified-Kind Alliance",
                verifiedAtUnspecified, "prior-admin", "manual");

            var result = await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.Inserted);
        }

        await using var fresh = CreateDbContext();
        var reloaded = await fresh.Alliances.AsNoTracking().SingleAsync(a => a.Code == "ZU");

        Assert.True(reloaded.IsVerified);
        Assert.NotNull(reloaded.VerifiedAt);
        Assert.Equal(DateTimeKind.Utc, reloaded.VerifiedAt!.Value.Kind);
        Assert.Equal(DateTime.SpecifyKind(verifiedAtUnspecified, DateTimeKind.Utc), reloaded.VerifiedAt!.Value);
        Assert.Equal("prior-admin", reloaded.VerifiedBy);
        Assert.Equal("manual", reloaded.VerificationSource);
    }

    [Fact]
    public async Task Handle_ImportedRowCarryingLocalKindVerifiedAt_RoundTripsAsUtcThroughAFreshContext()
    {
        // A real instant expressed in the wrong zone — ImportVerification converts it
        // (ToUniversalTime), so the instant read back must equal the converted UTC value, not the
        // original local clock reading.
        var verifiedAtLocal = new DateTime(2026, 9, 14, 20, 31, 12, 123, DateTimeKind.Local);
        var expectedUtc = verifiedAtLocal.ToUniversalTime();

        await using (var db = CreateDbContext())
        {
            var handler = new ImportAlliancesCommandHandler(
                new AllianceRepository(db), new AdminContext { ActorId = "importing-admin" });
            var dto = new AllianceImportDto("ZL", "Zeta Local-Kind Alliance",
                verifiedAtLocal, "prior-admin", "manual");

            var result = await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.Inserted);
        }

        await using var fresh = CreateDbContext();
        var reloaded = await fresh.Alliances.AsNoTracking().SingleAsync(a => a.Code == "ZL");

        Assert.True(reloaded.IsVerified);
        Assert.NotNull(reloaded.VerifiedAt);
        Assert.Equal(DateTimeKind.Utc, reloaded.VerifiedAt!.Value.Kind);
        Assert.Equal(expectedUtc, reloaded.VerifiedAt!.Value);
        Assert.Equal("prior-admin", reloaded.VerifiedBy);
        Assert.Equal("manual", reloaded.VerificationSource);
    }
}
