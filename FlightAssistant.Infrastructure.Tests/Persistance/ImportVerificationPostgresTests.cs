using System.Text;
using FlightAssistant.API.Csv;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// The one thing about CSV-import verification (#362) InMemory cannot see: whether a
/// <c>VerifiedAt</c> value, parsed by the real <c>CsvImportParser</c>/<c>AllianceImportDtoMap</c>
/// pipeline, actually reaches Postgres and comes back <see cref="DateTimeKind.Utc"/> through a real
/// <c>timestamptz</c> column, read through a fresh <see cref="FlightAssistantDbContext"/> rather
/// than the tracked entity that wrote it. Npgsql refuses a <see cref="DateTimeKind.Unspecified"/>
/// value outright — the exact bug class CLAUDE.md documents for date query parameters — so a wrong
/// <c>DateTimeStyles</c> choice in the CSV parser would surface here as a hard failure, not a
/// silently-wrong value; InMemory accepts any <c>Kind</c> without complaint and would never catch
/// it. Going through the real parser (rather than constructing the DTO by hand) is what lets this
/// test — unlike a hand-built DTO would — actually fail if <c>CsvImportParser</c>'s
/// <c>DateTimeStyles</c> configuration regresses.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ImportVerificationPostgresTests : PostgresTestBase
{
    public ImportVerificationPostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Handle_ImportedRowCarryingVerifiedAt_RoundTripsAsUtcThroughAFreshContext()
    {
        const string csv = """
            Code,Name,VerifiedAt,VerifiedBy,VerificationSource
            ZR,Zeta Round-Trip Alliance,2026-09-14T20:31:12.1230000Z,prior-admin,manual
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var items = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(stream);
        var dto = Assert.Single(items);
        Assert.NotNull(dto.VerifiedAt);
        Assert.Equal(DateTimeKind.Utc, dto.VerifiedAt!.Value.Kind); // sanity check on the parse itself

        await using (var db = CreateDbContext())
        {
            var handler = new ImportAlliancesCommandHandler(
                new AllianceRepository(db), new AdminContext { ActorId = "importing-admin" });

            var result = await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.Inserted);
        }

        await using var fresh = CreateDbContext();
        var reloaded = await fresh.Alliances.AsNoTracking().SingleAsync(a => a.Code == "ZR");

        Assert.True(reloaded.IsVerified);
        Assert.NotNull(reloaded.VerifiedAt);
        Assert.Equal(DateTimeKind.Utc, reloaded.VerifiedAt!.Value.Kind);
        Assert.Equal(dto.VerifiedAt!.Value, reloaded.VerifiedAt!.Value);
        Assert.Equal("prior-admin", reloaded.VerifiedBy);
        Assert.Equal("manual", reloaded.VerificationSource);
    }
}
