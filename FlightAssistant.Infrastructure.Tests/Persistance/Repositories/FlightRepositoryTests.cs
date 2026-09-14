using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// Drives <see cref="FlightRepository.GetDueTrackedFlightIdsAsync"/> against real PostgreSQL (see
/// #295) to prove the <c>NextSyncDueUtc</c> filter and ordering actually translate to SQL the way
/// the C# comparison logic implies. This used to run on <c>UseInMemoryDatabase</c>, which
/// evaluates the <c>Where</c> clause client-side rather than translating it, so it proved the
/// comparison logic but never proved the translation itself.
/// </summary>
[Collection(PostgresCollection.Name)]
public class FlightRepositoryTests : PostgresTestBase
{
    public FlightRepositoryTests(PostgresContainerFixture fixture) : base(fixture) { }

    private FlightAssistantDbContext CreateContext() => CreateDbContext();

    private static FlightRepository Repository(FlightAssistantDbContext db, TimeProvider clock) => new(db, clock);

    /// <summary>
    /// Real PostgreSQL enforces the FK constraints <see cref="FlightConfiguration"/> and friends
    /// declare — unlike the InMemory provider this suite used to run on — so a <see cref="Flight"/>
    /// row needs real Airline/Airport/Country rows behind it, not just plausible-looking integers.
    /// Idempotent per <paramref name="db"/> (checks for an existing row before inserting) so
    /// repeated calls within one test, and the reset-per-test fixture, both stay cheap and safe.
    /// </summary>
    private static (int AirlineId, int DepartureAirportId, int ArrivalAirportId) EnsureReferenceData(
        FlightAssistantDbContext db)
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

    /// <summary>Idempotent per <paramref name="db"/> for the same <paramref name="travelerId"/> —
    /// <c>TravelerFlightConfiguration</c> FKs <c>traveler_flights.TravelerId</c> to a real
    /// <c>travelers</c> row under real Postgres.</summary>
    private static void EnsureTraveler(FlightAssistantDbContext db, Guid travelerId)
    {
        if (db.Travelers.Any(t => t.Id == travelerId)) return;
        db.Travelers.Add(Traveler.Register(travelerId, $"{travelerId:N}@test.local"));
        db.SaveChanges();
    }

    private static readonly DateTime SyncNow = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private static FakeTimeProvider ClockAt(DateTime utc) => new(new DateTimeOffset(utc));

    /// <summary>A saved flight with the given next sync time, tracked by one new traveler unless
    /// <paramref name="tracked"/> is false.</summary>
    private static Flight FlightDue(FlightAssistantDbContext db, string flightNumber, DateTime? nextSyncDueUtc, bool tracked = true)
    {
        var (airlineId, departureAirportId, arrivalAirportId) = EnsureReferenceData(db);
        var flight = Flight.Create(airlineId, flightNumber, departureAirportId, arrivalAirportId,
            new DateOnly(2026, 9, 14), SyncNow.AddHours(1), SyncNow.AddHours(3));
        if (nextSyncDueUtc is not null)
            flight.ApplySyncPlan(FlightSyncMode.Fast, nextSyncDueUtc);
        db.Flights.Add(flight);
        db.SaveChanges();

        if (tracked)
            AddTracker(db, flight);

        return flight;
    }

    private static void AddTracker(FlightAssistantDbContext db, Flight flight)
    {
        var travelerId = Guid.NewGuid();
        EnsureTraveler(db, travelerId);
        db.TravelerFlights.Add(TravelerFlight.Create(travelerId, flight.Id));
        db.SaveChanges();
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ReturnsDueFlights_OldestDueFirst()
    {
        using var db = CreateContext();
        // Inserted out of due order, so only an ORDER BY can produce the expected order.
        var dueAnHourAgo = FlightDue(db, "TK1", SyncNow.AddHours(-1));
        var dueTenMinutesAgo = FlightDue(db, "TK2", SyncNow.AddMinutes(-10));
        var dueHalfAnHourAgo = FlightDue(db, "TK3", SyncNow.AddMinutes(-30));

        var ids = await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync();

        Assert.Equal(new[] { dueAnHourAgo.Id, dueHalfAnHourAgo.Id, dueTenMinutesAgo.Id }, ids);
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_IncludesAFlightDueExactlyNow_ExcludesOneDueAMinuteLater()
    {
        using var db = CreateContext();
        var dueNow = FlightDue(db, "TK1", SyncNow);
        FlightDue(db, "TK2", SyncNow.AddMinutes(1));

        var ids = await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync();

        Assert.Equal(new[] { dueNow.Id }, ids);
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ExcludesAFlightWithNoNextSync()
    {
        using var db = CreateContext();
        FlightDue(db, "TK1", nextSyncDueUtc: null);

        Assert.Empty(await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ExcludesAnUntrackedFlight_EvenWhenDue()
    {
        using var db = CreateContext();
        FlightDue(db, "TK1", SyncNow.AddHours(-1), tracked: false);

        Assert.Empty(await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ReturnsAFlightOnce_WhenSeveralTravelersTrackIt()
    {
        using var db = CreateContext();
        var flight = FlightDue(db, "TK1", SyncNow.AddHours(-1));
        AddTracker(db, flight);

        Assert.Equal(new[] { flight.Id }, await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ReadsTheClockOnEveryCall()
    {
        using var db = CreateContext();
        var clock = ClockAt(SyncNow);
        var repository = Repository(db, clock);
        var flight = FlightDue(db, "TK1", SyncNow.AddMinutes(5));

        Assert.Empty(await repository.GetDueTrackedFlightIdsAsync());

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { flight.Id }, await repository.GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task IsFlightTrackedAsync_TrueOnlyForATrackedFlight()
    {
        using var db = CreateContext();
        var tracked = FlightDue(db, "TK1", null);
        var untracked = FlightDue(db, "TK2", null, tracked: false);
        var repository = Repository(db, ClockAt(SyncNow));

        Assert.True(await repository.IsFlightTrackedAsync(tracked.Id));
        Assert.False(await repository.IsFlightTrackedAsync(untracked.Id));
    }
}
