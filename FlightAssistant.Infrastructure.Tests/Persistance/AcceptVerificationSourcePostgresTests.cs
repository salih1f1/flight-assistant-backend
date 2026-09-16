using System.Globalization;
using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Accept-source (#378) against real PostgreSQL: the real <see cref="AcceptVerificationSourceCommandHandler"/>
/// over the real repositories, on one <c>DbContext</c> carrying the real <see cref="AdminAuditInterceptor"/>
/// with an admin actor, as an <c>/admin</c> request scope has. The API tests cannot show either behaviour
/// here: their host runs on InMemory, which enforces no unique index, and strips the interceptor.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AcceptVerificationSourcePostgresTests : PostgresTestBase
{
    private const string AdminId = "admin-oid-378";
    private const string Source = "ourairports";
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresContainerFixture _fixture;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(T0));

    public AcceptVerificationSourcePostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    private static Airport Heathrow() => Airport.Create("LHR", "EGLL", "London Heathrow", "London", "GB", 51.4706, -0.461941, 83);
    private static Airport Gatwick() => Airport.Create("LGW", "EGKK", "London Gatwick", "London", "GB", 51.1481, -0.190278, 202);

    /// <summary>
    /// The data column and the verification columns change in one save, so the interceptor writes one
    /// <c>Update</c> row (not an <c>Update</c> and a <c>Verify</c>), stamped with the admin. The request row
    /// is never audited: it is the check's own history (<c>NotAuditedTypes</c>).
    /// </summary>
    [Fact]
    public async Task Handle_WritesTheSourcesValueAndVerifiesInOneSave_AuditedAsOneAdminUpdate_AndNotTheRequest()
    {
        await SeedAsync(Country.Create("GB", "United Kingdom"));
        var heathrow = Heathrow();
        await SeedAsync(heathrow);
        var requestId = await SeedMismatchAsync(heathrow, nameof(Airport.Name), "London Heathrow", "Heathrow Airport");

        await using (var db = CreateAuditedDbContext())
            await Handler(db).Handle(new AcceptVerificationSourceCommand(requestId, Source, AdminId), CancellationToken.None);

        await using var verify = CreateDbContext();
        var airport = await verify.Airports.AsNoTracking().SingleAsync(a => a.Id == heathrow.Id);
        Assert.Equal("Heathrow Airport", airport.Name);
        Assert.True(airport.IsVerified);
        Assert.Equal(AdminId, airport.VerifiedBy);
        Assert.Equal(Source, airport.VerificationSource);
        Assert.Equal(T0, airport.VerifiedAt);

        var request = await verify.VerificationRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal(VerificationRequestStatus.Mismatch, request.Status);

        var audit = await verify.AdminAuditLogs.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(audit, row => row.EntityType == nameof(VerificationRequest));
        var entry = Assert.Single(audit);
        Assert.Equal(AdminAuditAction.Update, entry.Action);
        Assert.Equal(nameof(Airport), entry.EntityType);
        Assert.Equal(heathrow.Id.ToString(CultureInfo.InvariantCulture), entry.EntityId);
        Assert.Equal(AdminId, entry.ActorId);

        using var after = JsonDocument.Parse(entry.After!);
        Assert.Equal("Heathrow Airport", after.RootElement.GetProperty(nameof(Airport.Name)).GetString());
        Assert.True(after.RootElement.GetProperty(nameof(Airport.IsVerified)).GetBoolean());
        Assert.Equal(AdminId, after.RootElement.GetProperty(nameof(Airport.VerifiedBy)).GetString());
        Assert.Equal(Source, after.RootElement.GetProperty(nameof(Airport.VerificationSource)).GetString());
    }

    /// <summary>
    /// A new IATA code another airport already owns is refused by the handler's own lookup, as a 409, before
    /// anything is written. Without that lookup the save reaches <c>airports</c>' unique index and fails as a
    /// <see cref="DbUpdateException"/>: a 500.
    /// </summary>
    [Fact]
    public async Task Handle_AnIataThatAnotherAirportOwns_Is409_AndWritesNothing()
    {
        await SeedAsync(Country.Create("GB", "United Kingdom"));
        var (heathrow, gatwick) = (Heathrow(), Gatwick());
        await SeedAsync(heathrow, gatwick);
        var requestId = await SeedMismatchAsync(heathrow, nameof(Airport.Iata), "LHR", "LGW");

        DomainException refused;
        await using (var db = CreateAuditedDbContext())
            refused = await Assert.ThrowsAsync<DomainException>(() =>
                Handler(db).Handle(new AcceptVerificationSourceCommand(requestId, Source, AdminId), CancellationToken.None));

        Assert.Equal(409, refused.StatusCode);
        Assert.Equal($"Airport Iata LGW already belongs to Airport {gatwick.Id}.", refused.Message);

        await using var verify = CreateDbContext();
        var unchanged = await verify.Airports.AsNoTracking().SingleAsync(a => a.Id == heathrow.Id);
        Assert.Equal("LHR", unchanged.Iata);
        Assert.False(unchanged.IsVerified);
        Assert.Null(unchanged.VerifiedBy);
        Assert.Null(unchanged.VerificationSource);
        Assert.Empty(await verify.AdminAuditLogs.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A source's country code our <c>countries</c> table doesn't have (<c>XK</c>) is refused by the handler's
    /// own lookup, as a 409, before anything is written. The edit rules only check the code's format, so without
    /// that lookup the save reaches <c>airports</c>' foreign key to <c>countries</c> and fails as a
    /// <see cref="DbUpdateException"/>: a 500.
    /// </summary>
    [Fact]
    public async Task Handle_ACountryCodeThatIsNotInCountries_Is409_AndWritesNothing()
    {
        await SeedAsync(Country.Create("GB", "United Kingdom"));
        var heathrow = Heathrow();
        await SeedAsync(heathrow);
        var requestId = await SeedMismatchAsync(heathrow, nameof(Airport.CountryCode), "GB", "XK");

        DomainException refused;
        await using (var db = CreateAuditedDbContext())
            refused = await Assert.ThrowsAsync<DomainException>(() =>
                Handler(db).Handle(new AcceptVerificationSourceCommand(requestId, Source, AdminId), CancellationToken.None));

        Assert.Equal(409, refused.StatusCode);
        Assert.Equal("Airport CountryCode XK is not a country we have; add the country first.", refused.Message);

        await using var verify = CreateDbContext();
        var unchanged = await verify.Airports.AsNoTracking().SingleAsync(a => a.Id == heathrow.Id);
        Assert.Equal("GB", unchanged.CountryCode);
        Assert.False(unchanged.IsVerified);
        Assert.Null(unchanged.VerifiedBy);
        Assert.Null(unchanged.VerificationSource);
        Assert.Empty(await verify.AdminAuditLogs.AsNoTracking().ToListAsync());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private AcceptVerificationSourceCommandHandler Handler(FlightAssistantDbContext db) =>
        new(new VerificationRequestRepository(db, _time, new FakeJobOptions()), new AirportRepository(db), new AirlineRepository(db), new CountryRepository(db), _time);

    /// <summary>Program.cs's registration for an <c>/admin</c> request: the interceptor reads an actor-bearing admin context.</summary>
    private FlightAssistantDbContext CreateAuditedDbContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(new AdminAuditInterceptor(
                new AdminContext { ActorId = AdminId, ActorName = "Ada Admin", CorrelationId = "corr-378" }))
            .Options);

    private async Task SeedAsync(params object[] entities)
    {
        await using var db = CreateDbContext();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    /// <summary>A Mismatch check of <paramref name="airport"/> by <see cref="Source"/> that differed on one column.</summary>
    private async Task<long> SeedMismatchAsync(Airport airport, string column, string ours, string source)
    {
        var request = VerificationRequest.Create(
            VerificationEntityType.Airport, airport.Id.ToString(CultureInfo.InvariantCulture), new[] { Source },
            VerificationTrigger.Admin, AdminId, 0m, T0.AddHours(-1));
        var sourceValues = new Dictionary<string, string?>(VerificationCheckedColumns.Project(airport)) { [column] = source };
        request.MarkMismatch(
            VerificationJson.SerializeDifferences(new[] { new VerificationDifference(column, ours, source, Source) }),
            VerificationJson.SerializeSourceValues(new Dictionary<string, IReadOnlyDictionary<string, string?>> { [Source] = sourceValues }),
            T0.AddMinutes(-30));

        await using var db = CreateDbContext();
        db.VerificationRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }
}
