using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// #316 fix-round F2: every one of the ten repositories converted to
/// <see cref="PagedQueryableExtensions.Page{T}"/> passes its own <c>(page, pageSize)</c> in that
/// order — a mechanical, ten-times-repeated edit where swapped argument order is exactly the
/// plausible slip. <c>AdminAuditRepositoryTests</c>, <c>BrowsableDataRepositoryTests</c> and
/// <c>AirportRepositoryTests</c> already catch a swap at their own call sites as a side effect of
/// tests written for other reasons; <c>AdminTravelerRepository</c>'s is caught by existing API-level
/// coverage. The other six call sites — <see cref="AdminFlightRepository.GetAllAsync"/>,
/// <see cref="AirlineRepository.GetAllAsync"/>, <see cref="FlightRepository.GetTravelerFlightsAsync"/>,
/// <see cref="LoungeRepository.GetAllAsync"/>, and both paged methods on
/// <see cref="LoyaltyRepository"/> — had nothing that would fail if <c>.Page(page, pageSize)</c>
/// were swapped to <c>.Page(pageSize, page)</c> at the call site, confirmed by actually making that
/// swap and re-running the suite: it stayed green.
///
/// <para>
/// <b>The proof shape.</b> Seed exactly two rows and request <c>pageSize = 1</c> for both
/// <c>page = 1</c> and <c>page = 2</c>. Correctly wired, both calls return exactly one row (they
/// partition the two-row table). Swapped, the <c>page = 2</c> call passes <c>(1, 2)</c> into
/// <c>Page</c> — offset <c>(1 - 1) * 2 = 0</c>, take <c>2</c> — so it returns <i>both</i> rows
/// instead of one. Asserting <c>page = 2</c> returns exactly one row is therefore what actually
/// distinguishes correct from swapped; asserting the same for <c>page = 1</c> alone would not (with
/// <c>page = pageSize = 1</c> the swap is a no-op), which is why every test below checks the second
/// page's count specifically, not just the first's. This is a pure LINQ query-composition property —
/// unlike the overflow clamp itself, it does not depend on Postgres's OFFSET semantics — so it needs
/// no real database and does not depend on row order (each read's ORDER BY is pinned separately by
/// <c>PagedRepositoryDeterministicOrderTests</c>, #328).
/// </para>
///
/// <para>
/// <b>Why InMemory, not the Postgres harness the rest of this directory uses.</b> These six
/// repositories translate cleanly on both providers (plain <c>OrderBy</c>/<c>Include</c>/<c>Join</c>,
/// no <c>ToLower()</c> or reflection-built <c>EF.Property</c> expression like the two repositories
/// that do need Postgres), so InMemory proves the same wiring fact for a fraction of the cost — no
/// container, no FK rows, no reset-per-test truncation.
/// </para>
/// </summary>
public class PagedRepositoryArgumentOrderTests
{
    private static FlightAssistantDbContext CreateInMemoryContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task AdminFlightRepository_GetAllAsync_SecondPageOfTwo_ReturnsExactlyOneRow()
    {
        await using var db = CreateInMemoryContext();
        var airline = Airline.Create("TK", "THY", "Test Airline", "US");
        var departure = Airport.Create("JFK", "KJFK", "JFK Airport", "New York", "US");
        var arrival = Airport.Create("LAX", "KLAX", "LAX Airport", "Los Angeles", "US");
        db.Airlines.Add(airline);
        db.Airports.AddRange(departure, arrival);
        await db.SaveChangesAsync();

        db.Flights.AddRange(
            Flight.Create(airline.Id, "TK100", departure.Id, arrival.Id, new DateOnly(2026, 9, 1),
                new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)),
            Flight.Create(airline.Id, "TK200", departure.Id, arrival.Id, new DateOnly(2026, 9, 2),
                new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var repo = new AdminFlightRepository(db);
        var (firstPage, total) = await repo.GetAllAsync(page: 1, pageSize: 1);
        var (secondPage, _) = await repo.GetAllAsync(page: 2, pageSize: 1);

        Assert.Equal(2, total);
        Assert.Single(firstPage);
        Assert.Single(secondPage); // fails at 2 items if page/pageSize were ever swapped at the call site
    }

    [Fact]
    public async Task AirlineRepository_GetAllAsync_SecondPageOfTwo_ReturnsExactlyOneRow()
    {
        await using var db = CreateInMemoryContext();
        db.Airlines.AddRange(
            Airline.Create("AA", "AAL", "Airline A", "US"),
            Airline.Create("BB", "BBB", "Airline B", "US"));
        await db.SaveChangesAsync();

        var repo = new AirlineRepository(db);
        var (firstPage, total) = await repo.GetAllAsync(page: 1, pageSize: 1);
        var (secondPage, _) = await repo.GetAllAsync(page: 2, pageSize: 1);

        Assert.Equal(2, total);
        Assert.Single(firstPage);
        Assert.Single(secondPage);
    }

    [Fact]
    public async Task FlightRepository_GetTravelerFlightsAsync_SecondPageOfTwo_ReturnsExactlyOneRow()
    {
        await using var db = CreateInMemoryContext();
        var airline = Airline.Create("TK", "THY", "Test Airline", "US");
        var departure = Airport.Create("JFK", "KJFK", "JFK Airport", "New York", "US");
        var arrival = Airport.Create("LAX", "KLAX", "LAX Airport", "Los Angeles", "US");
        var traveler = Traveler.Register($"{Guid.NewGuid():N}@test.local");
        db.Airlines.Add(airline);
        db.Airports.AddRange(departure, arrival);
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var flight1 = Flight.Create(airline.Id, "TK100", departure.Id, arrival.Id, new DateOnly(2026, 9, 1),
            new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
        var flight2 = Flight.Create(airline.Id, "TK200", departure.Id, arrival.Id, new DateOnly(2026, 9, 2),
            new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));
        db.Flights.AddRange(flight1, flight2);
        await db.SaveChangesAsync();

        db.TravelerFlights.AddRange(
            TravelerFlight.Create(traveler.Id, flight1.Id),
            TravelerFlight.Create(traveler.Id, flight2.Id));
        await db.SaveChangesAsync();

        // TimeProvider/AeroApiOptions are unused by GetTravelerFlightsAsync (only
        // GetActiveTrackedFlightsAsync reads them) — dummy values are enough.
        var repo = new FlightRepository(db, TimeProvider.System, Options.Create(new AeroApiOptions { ApiKey = "dummy" }));
        var (firstPage, total) = await repo.GetTravelerFlightsAsync(traveler.Id, page: 1, pageSize: 1);
        var (secondPage, _) = await repo.GetTravelerFlightsAsync(traveler.Id, page: 2, pageSize: 1);

        Assert.Equal(2, total);
        Assert.Single(firstPage);
        Assert.Single(secondPage);
    }

    [Fact]
    public async Task LoungeRepository_GetAllAsync_SecondPageOfTwo_ReturnsExactlyOneRow()
    {
        await using var db = CreateInMemoryContext();
        var airport = Airport.Create("JFK", "KJFK", "JFK Airport", "New York", "US");
        db.Airports.Add(airport);
        await db.SaveChangesAsync();

        db.Lounges.AddRange(
            Lounge.Create(airport.Id, "Lounge A"),
            Lounge.Create(airport.Id, "Lounge B"));
        await db.SaveChangesAsync();

        var repo = new LoungeRepository(db);
        var (firstPage, total) = await repo.GetAllAsync(page: 1, pageSize: 1);
        var (secondPage, _) = await repo.GetAllAsync(page: 2, pageSize: 1);

        Assert.Equal(2, total);
        Assert.Single(firstPage);
        Assert.Single(secondPage);
    }

    [Fact]
    public async Task LoyaltyRepository_GetProgramsAsync_SecondPageOfTwo_ReturnsExactlyOneRow()
    {
        await using var db = CreateInMemoryContext();
        var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline Program");
        db.LoyaltyProgramKinds.Add(kind);
        await db.SaveChangesAsync();

        db.LoyaltyPrograms.AddRange(
            LoyaltyProgram.Create("PROG-A", "Program A", kind.Id),
            LoyaltyProgram.Create("PROG-B", "Program B", kind.Id));
        await db.SaveChangesAsync();

        var repo = new LoyaltyRepository(db);
        var (firstPage, total) = await repo.GetProgramsAsync(page: 1, pageSize: 1);
        var (secondPage, _) = await repo.GetProgramsAsync(page: 2, pageSize: 1);

        Assert.Equal(2, total);
        Assert.Single(firstPage);
        Assert.Single(secondPage);
    }

    /// <summary>
    /// The count-based assertion this file uses throughout does not depend on row order, only on
    /// how many rows each page returns. Two distinct <see cref="LoyaltyProgram"/>s back the two
    /// memberships rather than one: <c>TravelerLoyaltyMembership</c> is schema-bounded by
    /// <c>UNIQUE(TravelerId, ProgramId)</c>, so the same traveler cannot hold two memberships in the
    /// same program.
    /// </summary>
    [Fact]
    public async Task LoyaltyRepository_GetMembershipsForTravelerAsync_SecondPageOfTwo_ReturnsExactlyOneRow()
    {
        await using var db = CreateInMemoryContext();
        var traveler = Traveler.Register($"{Guid.NewGuid():N}@test.local");
        var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline Program");
        db.Travelers.Add(traveler);
        db.LoyaltyProgramKinds.Add(kind);
        await db.SaveChangesAsync();

        var program1 = LoyaltyProgram.Create("PROG-A", "Program A", kind.Id);
        var program2 = LoyaltyProgram.Create("PROG-B", "Program B", kind.Id);
        db.LoyaltyPrograms.AddRange(program1, program2);
        await db.SaveChangesAsync();

        db.TravelerLoyaltyMemberships.AddRange(
            TravelerLoyaltyMembership.Create(traveler.Id, program1.Id, "ACC-1"),
            TravelerLoyaltyMembership.Create(traveler.Id, program2.Id, "ACC-2"));
        await db.SaveChangesAsync();

        var repo = new LoyaltyRepository(db);
        var (firstPage, total) = await repo.GetMembershipsForTravelerAsync(traveler.Id, page: 1, pageSize: 1);
        var (secondPage, _) = await repo.GetMembershipsForTravelerAsync(traveler.Id, page: 2, pageSize: 1);

        Assert.Equal(2, total);
        Assert.Single(firstPage);
        Assert.Single(secondPage);
    }
}
