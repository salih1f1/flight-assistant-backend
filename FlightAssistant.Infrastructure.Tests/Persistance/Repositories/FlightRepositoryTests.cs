using FlightAssistant.Application.FlightSync;
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

    /// <summary>A saved flight with the given sync plan, tracked by one new traveler unless
    /// <paramref name="tracked"/> is false. A null <paramref name="mode"/> is a flight never planned.</summary>
    private static Flight FlightDue(FlightAssistantDbContext db, string flightNumber, DateTime? nextSyncDueUtc,
        bool tracked = true, FlightSyncMode? mode = FlightSyncMode.Fast)
    {
        var (airlineId, departureAirportId, arrivalAirportId) = EnsureReferenceData(db);
        var flight = Flight.Create(airlineId, flightNumber, departureAirportId, arrivalAirportId,
            new DateOnly(2026, 9, 14), SyncNow.AddHours(1), SyncNow.AddHours(3));
        flight.ApplySyncPlan(mode, nextSyncDueUtc);
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

    /// <summary>A saved flight scheduled to arrive at <paramref name="scheduledArrivalUtc"/>, tracked by
    /// <paramref name="travelerId"/> when one is given.</summary>
    private static Flight FlightArriving(FlightAssistantDbContext db, string flightNumber, DateTime scheduledArrivalUtc,
        Guid? travelerId = null)
    {
        var (airlineId, departureAirportId, arrivalAirportId) = EnsureReferenceData(db);
        var flight = Flight.Create(airlineId, flightNumber, departureAirportId, arrivalAirportId,
            DateOnly.FromDateTime(scheduledArrivalUtc), scheduledArrivalUtc.AddHours(-3), scheduledArrivalUtc);
        db.Flights.Add(flight);
        db.SaveChanges();

        if (travelerId is { } id)
        {
            EnsureTraveler(db, id);
            db.TravelerFlights.Add(TravelerFlight.Create(id, flight.Id));
            db.SaveChanges();
        }

        return flight;
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
    public async Task GetDueTrackedFlightIdsAsync_ReturnsANeverPlannedFlight_AheadOfAnOverdueOne()
    {
        using var db = CreateContext();
        // Inserted after the overdue flight, and PostgreSQL sorts a null due time last, so only an
        // explicit "unplanned first" order puts it ahead.
        var overdue = FlightDue(db, "TK1", SyncNow.AddHours(-1));
        var unplanned = FlightDue(db, "TK2", nextSyncDueUtc: null, mode: null);

        var ids = await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync();

        Assert.Equal(new[] { unplanned.Id, overdue.Id }, ids);
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ExcludesAnUntrackedNeverPlannedFlight()
    {
        using var db = CreateContext();
        FlightDue(db, "TK1", nextSyncDueUtc: null, tracked: false, mode: null);

        Assert.Empty(await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ExcludesAStoppedFlight()
    {
        using var db = CreateContext();
        FlightDue(db, "TK1", nextSyncDueUtc: null, mode: FlightSyncMode.Stopped);

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

    // ── CountActiveTrackedFlightsAsync (#380) ──

    // Non-default stop rules (#308): stop 20 min after arrival or cancellation, 12 h after an unreported arrival.
    private static readonly FlightSyncPlanOptions StopRules = new(
        FastInterval: TimeSpan.FromMinutes(3),
        LiveInterval: TimeSpan.FromMinutes(7),
        ScheduleCheckInterval: TimeSpan.FromHours(6),
        FastWindowBeforeDeparture: TimeSpan.FromHours(3),
        LiveWindowBeforeDeparture: TimeSpan.FromDays(1),
        StopAfterArrival: TimeSpan.FromMinutes(20),
        StopWithoutArrivalAfter: TimeSpan.FromHours(12));

    private static Task<int> CountActive(FlightAssistantDbContext db, Guid travelerId) =>
        Repository(db, ClockAt(SyncNow)).CountActiveTrackedFlightsAsync(
            travelerId, SyncNow, StopRules.StopAfterArrival, StopRules.StopWithoutArrivalAfter);

    /// <summary>How many of these flights the planner would still sync, so the count can be checked
    /// against the planner's own Stopped rule rather than a second copy of it.</summary>
    private static int PlannerStillSyncs(IEnumerable<Flight> flights) =>
        flights.Count(f => FlightSyncPlanner.Plan(f, isTracked: true, SyncNow, StopRules).Mode != FlightSyncMode.Stopped);

    [Fact]
    public async Task CountActiveTrackedFlightsAsync_CountsTheTravelersFlightsThePlannerStillSyncs()
    {
        using var db = CreateContext();
        var traveler = Guid.NewGuid();

        var upcoming = FlightArriving(db, "TK10", SyncNow.AddHours(3), traveler);                  // counts
        var landedRecently = FlightArriving(db, "TK11", SyncNow.AddHours(-1), traveler);           // counts: landed 5 min ago
        landedRecently.ApplyStatusUpdate("ARR", null, null, SyncNow.AddHours(-4), SyncNow.AddMinutes(-5), null, SyncNow);
        var landedLongAgo = FlightArriving(db, "TK12", SyncNow.AddHours(-1), traveler);            // doesn't: landed 25 min ago
        landedLongAgo.ApplyStatusUpdate("ARR", null, null, SyncNow.AddHours(-4), SyncNow.AddMinutes(-25), null, SyncNow);
        var cancelledRecently = FlightArriving(db, "TK13", SyncNow.AddHours(3), traveler);         // counts: cancelled 5 min ago
        cancelledRecently.ApplyStatusUpdate("CAN", null, null, null, null, null, SyncNow.AddMinutes(-5));
        var cancelledLongAgo = FlightArriving(db, "TK14", SyncNow.AddHours(3), traveler);          // doesn't: cancelled 25 min ago
        cancelledLongAgo.ApplyStatusUpdate("CAN", null, null, null, null, null, SyncNow.AddMinutes(-25));
        var unreported = FlightArriving(db, "TK15", SyncNow.AddHours(-11), traveler);              // counts: no arrival, 11 h past
        var unreportedLongAgo = FlightArriving(db, "TK16", SyncNow.AddHours(-13), traveler);       // doesn't: no arrival, 13 h past
        var estimateLater = FlightArriving(db, "TK17", SyncNow.AddHours(-13), traveler);           // counts: the estimate is 11 h past
        estimateLater.ApplyStatusUpdate("DEP", null, SyncNow.AddHours(-11), SyncNow.AddHours(-14), null, null, SyncNow);
        FlightArriving(db, "TK18", SyncNow.AddHours(3), Guid.NewGuid());                            // doesn't: another traveler's
        FlightArriving(db, "TK19", SyncNow.AddHours(3));                                            // doesn't: untracked
        db.SaveChanges();

        var travelersFlights = new[] { upcoming, landedRecently, landedLongAgo, cancelledRecently, cancelledLongAgo, unreported, unreportedLongAgo, estimateLater };

        Assert.Equal(5, await CountActive(db, traveler));
        Assert.Equal(PlannerStillSyncs(travelersFlights), await CountActive(db, traveler));
    }

    [Fact]
    public async Task CountActiveTrackedFlightsAsync_AFlightExactlyAtAStopBoundary_HasStopped()
    {
        using var db = CreateContext();
        var traveler = Guid.NewGuid();

        var landedAtBoundary = FlightArriving(db, "TK20", SyncNow.AddHours(-1), traveler);         // doesn't: landed exactly 20 min ago
        landedAtBoundary.ApplyStatusUpdate("ARR", null, null, SyncNow.AddHours(-4), SyncNow.AddMinutes(-20), null, SyncNow);
        var landedInside = FlightArriving(db, "TK21", SyncNow.AddHours(-1), traveler);             // counts: landed 19 min ago
        landedInside.ApplyStatusUpdate("ARR", null, null, SyncNow.AddHours(-4), SyncNow.AddMinutes(-19), null, SyncNow);
        var unreportedAtBoundary = FlightArriving(db, "TK22", SyncNow.AddHours(-12), traveler);    // doesn't: exactly 12 h past
        var unreportedInside = FlightArriving(db, "TK23", SyncNow.AddHours(-12).AddMinutes(1), traveler); // counts

        db.SaveChanges();

        var travelersFlights = new[] { landedAtBoundary, landedInside, unreportedAtBoundary, unreportedInside };

        Assert.Equal(2, await CountActive(db, traveler));
        Assert.Equal(PlannerStillSyncs(travelersFlights), await CountActive(db, traveler));
    }

    // ── Status events: the latest one, and ties on effective time (#351) ──

    private static int StatusId(FlightAssistantDbContext db, string code) =>
        db.FlightStatus.Where(s => s.Code == code).Select(s => s.Id).Single();

    [Fact]
    public async Task ADetailChange_PassesTheUniqueIndex_IsTheLatestEvent_AndIsListedAboveItsStatusEvent()
    {
        using var db = CreateContext();
        var flight = FlightArriving(db, "TK30", SyncNow.AddHours(3));
        var departed = StatusId(db, "DEP");

        var status = FlightStatusEvent.Create(flight.Id, departed, SyncNow, "AeroAPI", "fa-1", "A1", "T5");
        db.FlightStatusEvents.Add(status);
        db.SaveChanges();
        var gateChange = FlightStatusEvent.CreateDetailChange(flight.Id, departed, SyncNow, "AeroAPI", "fa-1", status.Id, "B7", "T5", null);
        db.FlightStatusEvents.Add(gateChange);
        db.SaveChanges(); // same flight, source, status and effective time: only the separate hash lets this through

        var repo = Repository(db, ClockAt(SyncNow));

        Assert.Equal(gateChange.Id, (await repo.GetLatestFlightStatusEventAsync(flight.Id))!.Id);
        Assert.Equal(new[] { gateChange.Id, status.Id }, (await repo.GetFlightStatusEventsAsync(flight.Id)).Select(e => e.Id));
        Assert.Equal(gateChange.Id, Assert.Single(await repo.GetRecentFlightStatusEventsAsync(flight.Id, limit: 1)).Id);
    }

    [Fact]
    public async Task EventLists_PutTheMostRecentlyRecordedEventFirst_EvenWhenItsEffectiveTimeIsEarlier()
    {
        using var db = CreateContext();
        var flight = FlightArriving(db, "TK32", SyncNow.AddHours(3));
        var scheduled = StatusId(db, "SCH");

        var first = FlightStatusEvent.Create(flight.Id, scheduled, SyncNow, "AeroAPI", "fa-2", "A1", "T5");
        db.FlightStatusEvents.Add(first);
        db.SaveChanges();
        // The estimate moves earlier: recorded later, with an earlier effective time.
        var second = FlightStatusEvent.Create(flight.Id, scheduled, SyncNow.AddMinutes(-5), "AeroAPI", "fa-2", "A1", "T5");
        db.FlightStatusEvents.Add(second);
        db.SaveChanges();

        var repo = Repository(db, ClockAt(SyncNow));

        Assert.Equal(new[] { second.Id, first.Id }, (await repo.GetFlightStatusEventsAsync(flight.Id)).Select(e => e.Id));
        Assert.Equal(second.Id, Assert.Single(await repo.GetRecentFlightStatusEventsAsync(flight.Id, limit: 1)).Id);
        Assert.Equal(second.Id, (await repo.GetLatestFlightStatusEventAsync(flight.Id))!.Id);
    }

    [Fact]
    public async Task GetLatestFlightStatusEventAsync_AFlightWithNoEvents_ReturnsNull()
    {
        using var db = CreateContext();
        var flight = FlightArriving(db, "TK31", SyncNow.AddHours(3));

        Assert.Null(await Repository(db, ClockAt(SyncNow)).GetLatestFlightStatusEventAsync(flight.Id));
    }
}
