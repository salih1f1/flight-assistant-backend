using System.Data.Common;
using FlightAssistant.Application.Queries.Admin.Flights;
using FlightAssistant.Application.Queries.Admin.Travelers;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// #322's "one query, not N+1" rule, proved against real PostgreSQL rather than the InMemory
/// provider every other admin-endpoint integration test runs on: InMemory never issues a
/// <see cref="DbCommand"/> at all, so a <see cref="DbCommandInterceptor"/> — the mechanism this
/// file counts queries with — could never observe anything there. This also covers the one part of
/// the flight drill-down <c>GetAdminFlightDetailQueryHandlerTests</c> (Application.Tests) explicitly
/// cannot: <see cref="FlightStatusEvent.Status"/> is a private-setter navigation only EF's own
/// materializer populates, so newest-first ordering, "current status is the newest event", and the
/// provider's raw status extracted from <c>RawPayloadJson</c> are only observable here, driving the
/// real handler against real repositories with a real <c>Include</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminDrilldownRepositoryTests : PostgresTestBase
{
    private readonly PostgresContainerFixture _fixture;

    public AdminDrilldownRepositoryTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>A context wired with a real, counting <see cref="DbCommandInterceptor"/> — separate
    /// from <see cref="PostgresTestBase.CreateDbContext"/>, which carries no interceptor and is used
    /// for seeding below, exactly so seeding never pollutes the count under test.</summary>
    private (FlightAssistantDbContext Db, CommandCountingInterceptor Counter) CreateCountingDbContext()
    {
        var counter = new CommandCountingInterceptor();
        var db = new FlightAssistantDbContext(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options);
        return (db, counter);
    }

    /// <summary>Idempotent per <paramref name="db"/>, mirroring <c>FlightRepositoryTests</c>' own
    /// helper — real Postgres enforces the FK constraints a <see cref="Flight"/> row needs behind
    /// it.</summary>
    private static (int AirlineId, int DepartureAirportId, int ArrivalAirportId) EnsureReferenceData(FlightAssistantDbContext db)
    {
        var existingAirline = db.Airlines.FirstOrDefault();
        if (existingAirline is not null)
        {
            var airports = db.Airports.OrderBy(a => a.Id).Take(2).ToList();
            return (existingAirline.Id, airports[0].Id, airports[1].Id);
        }

        db.Countries.Add(Country.Create("US", "United States"));
        var airline = Airline.Create("TK", "THY", "Test Airline", "US");
        var departure = Airport.Create("JFK", "KJFK", "JFK Airport", "New York", "US");
        var arrival = Airport.Create("LAX", "KLAX", "LAX Airport", "Los Angeles", "US");
        db.Airlines.Add(airline);
        db.Airports.Add(departure);
        db.Airports.Add(arrival);
        db.SaveChanges();

        return (airline.Id, departure.Id, arrival.Id);
    }

    /// <summary>
    /// <paramref name="count"/> freshly-created, distinct <see cref="LoyaltyProgram"/> rows —
    /// never reused across calls — because <c>TravelerLoyaltyMembershipConfiguration</c> puts a
    /// unique index on <c>(TravelerId, ProgramId)</c>, so seeding several memberships for the same
    /// traveler needs one real program per membership, not one shared id. <c>Code</c> is
    /// <c>HasMaxLength(16)</c>, hence the truncated, still-effectively-unique code below rather than
    /// a bare <see cref="Guid"/>.
    /// </summary>
    private static List<int> CreateLoyaltyPrograms(FlightAssistantDbContext db, int count)
    {
        var kind = db.LoyaltyProgramKinds.FirstOrDefault()
            ?? db.LoyaltyProgramKinds.Add(LoyaltyProgramKind.Create("AIRLINE", "Airline")).Entity;
        db.SaveChanges();

        var programs = Enumerable.Range(0, count)
            .Select(i => LoyaltyProgram.Create(("P" + i + Guid.NewGuid().ToString("N"))[..16], $"Program {i}", kind.Id))
            .ToList();
        db.LoyaltyPrograms.AddRange(programs);
        db.SaveChanges();

        return programs.Select(p => p.Id).ToList();
    }

    // ══════════════════════════════ Traveler detail ══════════════════════════════

    /// <summary>Seeds a traveler with <paramref name="relatedCount"/> device tokens, tracked
    /// flights and loyalty memberships apiece, batched into four <c>SaveChangesAsync</c> calls
    /// total regardless of <paramref name="relatedCount"/> (flights and loyalty programs both need
    /// their store-generated ids before the tracking/membership rows that reference them can be
    /// built).</summary>
    private async Task<Guid> SeedTravelerWithRelationshipsAsync(int relatedCount)
    {
        await using var seed = CreateDbContext();
        var (airlineId, departureAirportId, arrivalAirportId) = EnsureReferenceData(seed);
        var programIds = CreateLoyaltyPrograms(seed, relatedCount);

        var traveler = Traveler.Register($"{Guid.NewGuid():N}@example.com", "Detail Traveler");
        for (var i = 0; i < relatedCount; i++)
            traveler.AddDeviceToken(i % 2 == 0 ? "ios" : "android", $"token-{i}-{Guid.NewGuid():N}");
        seed.Travelers.Add(traveler);
        await seed.SaveChangesAsync();

        var flights = Enumerable.Range(0, relatedCount)
            .Select(i => Flight.Create(
                airlineId, $"TK{100 + i}", departureAirportId, arrivalAirportId,
                new DateOnly(2026, 9, 10).AddDays(i),
                DateTime.UtcNow.AddDays(i), DateTime.UtcNow.AddDays(i).AddHours(2)))
            .ToList();
        seed.Flights.AddRange(flights);
        await seed.SaveChangesAsync();

        seed.TravelerFlights.AddRange(flights.Select(f => TravelerFlight.Create(traveler.Id, f.Id)));
        seed.TravelerLoyaltyMemberships.AddRange(programIds.Select((programId, i) =>
            TravelerLoyaltyMembership.Create(
                traveler.Id, programId, accountNumber: $"ACCT-{i}-{Guid.NewGuid():N}", tierId: null)));
        await seed.SaveChangesAsync();

        return traveler.Id;
    }

    /// <summary>
    /// The core "no N+1" proof for the traveler drill-down: driving the real
    /// <see cref="GetAdminTravelerDetailQueryHandler"/> against real
    /// <see cref="AdminTravelerRepository"/>/<see cref="FlightRepository"/>/<see cref="LoyaltyRepository"/>
    /// instances issues exactly the same, fixed number of SQL statements — one per repository call,
    /// each resolving its relationship server-side via a join or <c>Include</c> — whether the
    /// traveler has 1 related row of each kind or 5. A query-per-related-row (the N+1 shape #322
    /// forbids) would instead make the count grow with <paramref name="relatedCount"/>.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task TravelerDetail_QueryCount_StaysFixed_RegardlessOfRelatedRowCount(int relatedCount)
    {
        var travelerId = await SeedTravelerWithRelationshipsAsync(relatedCount);

        var (db, counter) = CreateCountingDbContext();
        await using (db)
        {
            var handler = new GetAdminTravelerDetailQueryHandler(
                new AdminTravelerRepository(db),
                new FlightRepository(db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" })),
                new LoyaltyRepository(db));

            var result = await handler.Handle(new GetAdminTravelerDetailQuery(travelerId), CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(relatedCount, result!.DeviceTokens.Count);
            Assert.Equal(relatedCount, result.TrackedFlights.Count);
            Assert.Equal(relatedCount, result.LoyaltyMemberships.Count);

            // Exactly 3: GetDetailAsync (traveler + tokens via one Include), GetRecentTravelerFlightsAsync
            // (tracked flights joined with Flight, capped server-side), GetAllMembershipsForTravelerAsync.
            // Fixed at 3 for both 1 and 5 related rows — the whole point of this test.
            Assert.Equal(3, counter.Count);
        }
    }

    [Fact]
    public async Task TravelerDetail_UnknownId_ReturnsNull_WithoutQueryingFlightsOrMemberships()
    {
        var (db, counter) = CreateCountingDbContext();
        await using (db)
        {
            var handler = new GetAdminTravelerDetailQueryHandler(
                new AdminTravelerRepository(db),
                new FlightRepository(db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" })),
                new LoyaltyRepository(db));

            var result = await handler.Handle(new GetAdminTravelerDetailQuery(Guid.NewGuid()), CancellationToken.None);

            Assert.Null(result);
            Assert.Equal(1, counter.Count); // only the traveler lookup — short-circuits before the rest
        }
    }

    /// <summary>
    /// #322 fix round, M5: the cap itself, proved as a real server-side <c>LIMIT</c> rather than a
    /// client-side truncation — mirrors <c>GetRecentFlightStatusEventsAsync_CapsServerSide_...</c>
    /// below for the flight side. 4 tracked flights exist, the repository is asked for 2, and
    /// exactly the 2 most recently-serviced come back, in a single query.
    /// </summary>
    [Fact]
    public async Task GetRecentTravelerFlightsAsync_CapsServerSide_AndReturnsOnlyTheNewestRows()
    {
        Guid travelerId;
        await using (var seedDb = CreateDbContext())
        {
            var (airlineId, departureAirportId, arrivalAirportId) = EnsureReferenceData(seedDb);
            var traveler = Traveler.Register($"{Guid.NewGuid():N}@example.com");
            seedDb.Travelers.Add(traveler);
            await seedDb.SaveChangesAsync();

            var flights = Enumerable.Range(0, 4)
                .Select(i => Flight.Create(
                    airlineId, $"TK{200 + i}", departureAirportId, arrivalAirportId,
                    new DateOnly(2026, 9, 10).AddDays(i),
                    DateTime.UtcNow.AddDays(i), DateTime.UtcNow.AddDays(i).AddHours(2)))
                .ToList();
            seedDb.Flights.AddRange(flights);
            await seedDb.SaveChangesAsync();

            seedDb.TravelerFlights.AddRange(flights.Select(f => TravelerFlight.Create(traveler.Id, f.Id)));
            await seedDb.SaveChangesAsync();

            travelerId = traveler.Id;
        }

        var (db, counter) = CreateCountingDbContext();
        await using (db)
        {
            var repo = new FlightRepository(db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" }));

            var result = await repo.GetRecentTravelerFlightsAsync(travelerId, limit: 2, CancellationToken.None);

            Assert.Equal(2, result.Count);
            // The 2 newest by ServiceDateUtc: day+3 then day+2 (of the day+0..day+3 series seeded above).
            Assert.Equal(new DateOnly(2026, 9, 13), result[0].Flight.ServiceDateUtc);
            Assert.Equal(new DateOnly(2026, 9, 12), result[1].Flight.ServiceDateUtc);
            Assert.Equal(1, counter.Count);
        }
    }

    /// <summary>
    /// #322 fix round, M4: memberships and device tokens now carry a deterministic order —
    /// <c>ProgramId</c> ascending (a real SQL <c>ORDER BY</c> in <c>LoyaltyRepository</c>) and
    /// <c>LastSeenUtc</c> ascending (sorted in the handler, since device tokens are loaded through
    /// the same <c>Include</c> as the <c>Traveler</c> row). Both are seeded in the reverse of their
    /// expected output order — reverse program-id insertion, reverse chronological token insertion —
    /// so a passing assertion proves the fix re-orders them rather than merely preserving whatever
    /// order they happened to be inserted in.
    /// </summary>
    [Fact]
    public async Task TravelerDetail_MembershipsAndDeviceTokens_AreOrderedDeterministically()
    {
        Guid travelerId;
        List<int> programIds;
        await using (var seedDb = CreateDbContext())
        {
            programIds = CreateLoyaltyPrograms(seedDb, 3); // ascending ids: p0 < p1 < p2

            var traveler = Traveler.Register($"{Guid.NewGuid():N}@example.com");
            traveler.AddDeviceToken("web", "token-newest", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
            traveler.AddDeviceToken("android", "token-oldest", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            traveler.AddDeviceToken("ios", "token-middle", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            seedDb.Travelers.Add(traveler);
            await seedDb.SaveChangesAsync();

            // Inserted in reverse of ascending ProgramId order: p2, p0, p1.
            seedDb.TravelerLoyaltyMemberships.Add(TravelerLoyaltyMembership.Create(
                traveler.Id, programIds[2], accountNumber: $"ACCT-2-{Guid.NewGuid():N}", tierId: null));
            seedDb.TravelerLoyaltyMemberships.Add(TravelerLoyaltyMembership.Create(
                traveler.Id, programIds[0], accountNumber: $"ACCT-0-{Guid.NewGuid():N}", tierId: null));
            seedDb.TravelerLoyaltyMemberships.Add(TravelerLoyaltyMembership.Create(
                traveler.Id, programIds[1], accountNumber: $"ACCT-1-{Guid.NewGuid():N}", tierId: null));
            await seedDb.SaveChangesAsync();

            travelerId = traveler.Id;
        }

        var (db, _) = CreateCountingDbContext();
        await using (db)
        {
            var handler = new GetAdminTravelerDetailQueryHandler(
                new AdminTravelerRepository(db),
                new FlightRepository(db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" })),
                new LoyaltyRepository(db));

            var result = await handler.Handle(new GetAdminTravelerDetailQuery(travelerId), CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(3, result!.LoyaltyMemberships.Count);
            // Compared against the known ascending program-id sequence, not a same-expression
            // OrderBy-of-itself — that would trivially pass regardless of whether the fix works.
            Assert.Equal(programIds, result.LoyaltyMemberships.Select(m => m.ProgramId).ToList());

            Assert.Equal(3, result.DeviceTokens.Count);
            Assert.Equal(["android", "ios", "web"], result.DeviceTokens.Select(t => t.Platform));
        }
    }

    // ═══════════════════════════════ Flight detail ════════════════════════════════

    private async Task<int> SeedFlightAsync(FlightAssistantDbContext db, string flightNumber = "TK900")
    {
        var (airlineId, departureAirportId, arrivalAirportId) = EnsureReferenceData(db);
        var flight = Flight.Create(airlineId, flightNumber, departureAirportId, arrivalAirportId,
            new DateOnly(2026, 9, 10), DateTime.UtcNow, DateTime.UtcNow.AddHours(2));
        db.Flights.Add(flight);
        await db.SaveChangesAsync();
        return flight.Id;
    }

    private static async Task<int> StatusIdAsync(FlightAssistantDbContext db, string code) =>
        await db.FlightStatus.Where(s => s.Code == code).Select(s => s.Id).SingleAsync();

    [Fact]
    public async Task FlightDetail_UnknownId_ReturnsNull_WithoutQueryingStatusEvents()
    {
        var (db, counter) = CreateCountingDbContext();
        await using (db)
        {
            var handler = new GetAdminFlightDetailQueryHandler(new AdminFlightRepository(db), new FlightRepository(
                db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" })));

            var result = await handler.Handle(new GetAdminFlightDetailQuery(999_999), CancellationToken.None);

            Assert.Null(result);
            Assert.Equal(1, counter.Count); // only the flight lookup
        }
    }

    /// <summary>
    /// Drives the real handler end-to-end to prove what no mock-based test can: the current status
    /// really is the newest event, history really is newest-first, and the provider's raw status is
    /// really pulled out of <c>RawPayloadJson</c> — null for the older, manually-created event with
    /// no payload at all ("where recorded"), populated for the newer AeroAPI-sourced one.
    /// </summary>
    [Fact]
    public async Task FlightDetail_CurrentStatusIsTheNewestEvent_HistoryIsNewestFirst_AndProviderRawStatusComesFromRawPayload()
    {
        int flightId;
        await using (var seedDb = CreateDbContext())
        {
            flightId = await SeedFlightAsync(seedDb);
            var scheduledId = await StatusIdAsync(seedDb, "SCH");
            var departedId = await StatusIdAsync(seedDb, "DEP");

            var older = FlightStatusEvent.Create(
                flightId, scheduledId, new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc),
                source: "manual"); // no raw payload at all
            var newer = FlightStatusEvent.Create(
                flightId, departedId, new DateTime(2026, 9, 10, 7, 0, 0, DateTimeKind.Utc),
                source: "AeroAPI", rawPayloadJson: "{\"status\":\"En Route / On Time\",\"other\":1}");
            seedDb.FlightStatusEvents.AddRange(older, newer);
            await seedDb.SaveChangesAsync();
        }

        var (queryDb, _) = CreateCountingDbContext();
        await using (queryDb)
        {
            var handler = new GetAdminFlightDetailQueryHandler(new AdminFlightRepository(queryDb), new FlightRepository(
                queryDb, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" })));

            var result = await handler.Handle(new GetAdminFlightDetailQuery(flightId), CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(2, result!.StatusHistory.Count);

            // Newest first: DEP (7:00) before SCH (6:00).
            Assert.Equal("DEP", result.StatusHistory[0].StatusCode);
            Assert.Equal("SCH", result.StatusHistory[1].StatusCode);

            // CurrentStatus is simply the newest event.
            Assert.Equal(result.StatusHistory[0], result.CurrentStatus);
            Assert.Equal("DEP", result.CurrentStatus!.StatusCode);

            // The provider's raw status: populated from RawPayloadJson for the AeroAPI-sourced
            // event, null — "where recorded" — for the manual one with no payload at all.
            Assert.Equal("En Route / On Time", result.CurrentStatus.ProviderRawStatus);
            Assert.Null(result.StatusHistory[1].ProviderRawStatus);

            // Well under the cap — #322 fix round, M3.
            Assert.False(result.StatusHistoryTruncated);
        }
    }

    /// <summary>
    /// The boundary the probe row exists for: a flight with <em>precisely</em>
    /// <see cref="GetAdminFlightDetailQueryHandler.MaxStatusHistory"/> events is complete, not
    /// truncated. Inferring truncation from <c>Count == Max</c> would flag this response and send
    /// an operator to the generic browser for rows that are not there. This test fails under that
    /// inference and passes under the fetch-one-extra rule, which is the whole point of it.
    /// </summary>
    [Fact]
    public async Task FlightDetail_HistoryExactlyAtTheCap_LeavesStatusHistoryTruncatedFalse()
    {
        int flightId;
        await using (var seedDb = CreateDbContext())
        {
            flightId = await SeedFlightAsync(seedDb);
            var scheduledId = await StatusIdAsync(seedDb, "SCH");

            var baseTime = new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc);
            var events = Enumerable.Range(0, GetAdminFlightDetailQueryHandler.MaxStatusHistory)
                .Select(i => FlightStatusEvent.Create(flightId, scheduledId, baseTime.AddMinutes(i), source: "manual"))
                .ToList();
            seedDb.FlightStatusEvents.AddRange(events);
            await seedDb.SaveChangesAsync();
        }

        var (db, _) = CreateCountingDbContext();
        await using (db)
        {
            var handler = new GetAdminFlightDetailQueryHandler(new AdminFlightRepository(db), new FlightRepository(
                db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" })));

            var result = await handler.Handle(new GetAdminFlightDetailQuery(flightId), CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(GetAdminFlightDetailQueryHandler.MaxStatusHistory, result!.StatusHistory.Count);
            Assert.False(result.StatusHistoryTruncated);
        }
    }

    /// <summary>
    /// #322 fix round, M3: with MORE events than the cap, <c>StatusHistoryTruncated</c>
    /// must say so — a caller reading only the JSON cannot otherwise tell "this flight had exactly
    /// 50 events" apart from "there are more, ask the browser". 51 events are seeded and exactly
    /// <see cref="GetAdminFlightDetailQueryHandler.MaxStatusHistory"/> come back, proving the flag
    /// tracks what was actually returned, not merely what was seeded.
    /// </summary>
    [Fact]
    public async Task FlightDetail_HistoryOverTheCap_SetsStatusHistoryTruncatedTrue()
    {
        int flightId;
        await using (var seedDb = CreateDbContext())
        {
            flightId = await SeedFlightAsync(seedDb);
            var scheduledId = await StatusIdAsync(seedDb, "SCH");

            var baseTime = new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc);
            var events = Enumerable.Range(0, GetAdminFlightDetailQueryHandler.MaxStatusHistory + 1)
                .Select(i => FlightStatusEvent.Create(flightId, scheduledId, baseTime.AddMinutes(i), source: "manual"))
                .ToList();
            seedDb.FlightStatusEvents.AddRange(events);
            await seedDb.SaveChangesAsync();
        }

        var (db, _) = CreateCountingDbContext();
        await using (db)
        {
            var handler = new GetAdminFlightDetailQueryHandler(new AdminFlightRepository(db), new FlightRepository(
                db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" })));

            var result = await handler.Handle(new GetAdminFlightDetailQuery(flightId), CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(GetAdminFlightDetailQueryHandler.MaxStatusHistory, result!.StatusHistory.Count);
            Assert.True(result.StatusHistoryTruncated);
        }
    }

    /// <summary>
    /// The cap itself, proved as a real server-side <c>LIMIT</c> rather than a client-side
    /// truncation: 4 events exist, the repository is asked for 2, and exactly 2 come back — the 2
    /// most recent — in a single query.
    /// </summary>
    [Fact]
    public async Task GetRecentFlightStatusEventsAsync_CapsServerSide_AndReturnsOnlyTheNewestRows()
    {
        int flightId;
        await using (var seedDb = CreateDbContext())
        {
            flightId = await SeedFlightAsync(seedDb);
            var scheduledId = await StatusIdAsync(seedDb, "SCH");

            var baseTime = new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc);
            var events = Enumerable.Range(0, 4)
                .Select(i => FlightStatusEvent.Create(flightId, scheduledId, baseTime.AddMinutes(i * 10), source: "manual"))
                .ToList();
            seedDb.FlightStatusEvents.AddRange(events);
            await seedDb.SaveChangesAsync();
        }

        var (db, counter) = CreateCountingDbContext();
        await using (db)
        {
            var repo = new FlightRepository(db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" }));

            var result = await repo.GetRecentFlightStatusEventsAsync(flightId, limit: 2, CancellationToken.None);

            Assert.Equal(2, result.Count);
            // The 2 newest: minute 30 then minute 20 (of the 0/10/20/30 series seeded above).
            Assert.Equal(new DateTime(2026, 9, 10, 6, 30, 0, DateTimeKind.Utc), result[0].EffectiveAtUtc);
            Assert.Equal(new DateTime(2026, 9, 10, 6, 20, 0, DateTimeKind.Utc), result[1].EffectiveAtUtc);
            Assert.Equal(1, counter.Count);
        }
    }

    /// <summary>Mirrors the traveler-side "no N+1" proof: the flight drill-down's query count is
    /// fixed at 2 (the flight row, the capped status-event page) regardless of how many status
    /// events actually exist for the flight.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task FlightDetail_QueryCount_StaysFixed_RegardlessOfStatusEventCount(int eventCount)
    {
        int flightId;
        await using (var seedDb = CreateDbContext())
        {
            flightId = await SeedFlightAsync(seedDb);
            var scheduledId = await StatusIdAsync(seedDb, "SCH");

            var baseTime = new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc);
            var events = Enumerable.Range(0, eventCount)
                .Select(i => FlightStatusEvent.Create(flightId, scheduledId, baseTime.AddMinutes(i), source: "manual"))
                .ToList();
            seedDb.FlightStatusEvents.AddRange(events);
            await seedDb.SaveChangesAsync();
        }

        var (db, counter) = CreateCountingDbContext();
        await using (db)
        {
            var handler = new GetAdminFlightDetailQueryHandler(new AdminFlightRepository(db), new FlightRepository(
                db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" })));

            var result = await handler.Handle(new GetAdminFlightDetailQuery(flightId), CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(eventCount, result!.StatusHistory.Count);
            Assert.Equal(2, counter.Count);
        }
    }
}
