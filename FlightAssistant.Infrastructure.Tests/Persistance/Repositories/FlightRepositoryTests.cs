using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// Drives <see cref="FlightRepository.GetActiveTrackedFlightsAsync"/> against
/// <c>UseInMemoryDatabase</c> to prove the LINQ filtering logic (which flights come back).
///
/// <para>
/// <b>What this does and does not prove.</b> The InMemory provider evaluates the
/// <c>Where</c> clause in-memory rather than translating it to SQL, so these tests prove the
/// C# comparison logic is correct but cannot prove a <see cref="DateOnly"/> comparison actually
/// translates to real PostgreSQL the same way — see issue #295. A <see cref="DateOnly"/>
/// range comparison is about as simple as EF translation gets (no custom value converter
/// quirks, no client-eval fallback), so the residual risk is low, but it is unproven by this
/// suite.
/// </para>
/// </summary>
public class FlightRepositoryTests
{
    private static FlightAssistantDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static FlightRepository Repository(
        FlightAssistantDbContext db, TimeProvider clock, int statusWindowFutureDays = 2) =>
        new(db, clock, Options.Create(new AeroApiOptions
        {
            ApiKey = "dummy",
            StatusWindowFutureDays = statusWindowFutureDays
        }));

    private static Flight TrackedFlight(FlightAssistantDbContext db, Guid travelerId, DateOnly serviceDate, string flightNumber)
    {
        var flight = Flight.Create(
            airlineId: 1,
            flightNumber: flightNumber,
            departureAirportId: 1,
            arrivalAirportId: 2,
            serviceDate: serviceDate,
            scheduledDepartureUtc: serviceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            scheduledArrivalUtc: serviceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(2));

        db.Flights.Add(flight);
        db.SaveChanges();

        db.TravelerFlights.Add(TravelerFlight.Create(travelerId, flight.Id));
        db.SaveChanges();

        return flight;
    }

    [Fact]
    public async Task GetActiveTrackedFlightsAsync_ReturnsAFlight_InsideTheWindow()
    {
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var travelerId = Guid.NewGuid();

        TrackedFlight(db, travelerId, new DateOnly(2026, 9, 2), "TK1028");

        var result = await Repository(db, clock).GetActiveTrackedFlightsAsync();

        Assert.Single(result);
    }

    /// <summary>
    /// The issue's own acceptance criterion: a flight tracked 6 months out must not be a poll
    /// candidate at all, since AeroAPI's status endpoint can never answer for it.
    /// </summary>
    [Fact]
    public async Task GetActiveTrackedFlightsAsync_ExcludesAFlightTrackedSixMonthsOut()
    {
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var travelerId = Guid.NewGuid();

        TrackedFlight(db, travelerId, new DateOnly(2027, 3, 1), "TK1028");

        var result = await Repository(db, clock).GetActiveTrackedFlightsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActiveTrackedFlightsAsync_ExcludesAFlightOneDayPastThePaddedUpperBound()
    {
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var travelerId = Guid.NewGuid();

        // StatusWindowFutureDays defaults to 2, padded by 1 => upper bound is today+3 (2026-09-04).
        // One day past that is 2026-09-05.
        TrackedFlight(db, travelerId, new DateOnly(2026, 9, 5), "TK1028");

        var result = await Repository(db, clock).GetActiveTrackedFlightsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActiveTrackedFlightsAsync_IncludesAFlightExactlyAtThePaddedUpperBound()
    {
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var travelerId = Guid.NewGuid();

        // today (2026-09-01) + StatusWindowFutureDays (2) + 1 day pad = 2026-09-04, included.
        TrackedFlight(db, travelerId, new DateOnly(2026, 9, 4), "TK1028");

        var result = await Repository(db, clock).GetActiveTrackedFlightsAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetActiveTrackedFlightsAsync_IncludesAFlightScheduledForToday()
    {
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var travelerId = Guid.NewGuid();

        TrackedFlight(db, travelerId, new DateOnly(2026, 9, 1), "TK1028");

        var result = await Repository(db, clock).GetActiveTrackedFlightsAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetActiveTrackedFlightsAsync_ExcludesAFlightThatDepartedYesterday()
    {
        // The lower bound is deliberately left untouched by this issue — widening it would
        // increase spend, the opposite of what #283 is for. Documents current behaviour, not an
        // endorsement (see FlightRepository.GetActiveTrackedFlightsAsync's doc comment).
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var travelerId = Guid.NewGuid();

        TrackedFlight(db, travelerId, new DateOnly(2026, 8, 31), "TK1028");

        var result = await Repository(db, clock).GetActiveTrackedFlightsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActiveTrackedFlightsAsync_ExcludesAnUntrackedFlight_EvenInsideTheWindow()
    {
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));

        var flight = Flight.Create(
            airlineId: 1,
            flightNumber: "TK1028",
            departureAirportId: 1,
            arrivalAirportId: 2,
            serviceDate: new DateOnly(2026, 9, 2),
            scheduledDepartureUtc: new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            scheduledArrivalUtc: new DateTime(2026, 9, 2, 2, 0, 0, DateTimeKind.Utc));
        db.Flights.Add(flight);
        db.SaveChanges();
        // Deliberately no TravelerFlight row.

        var result = await Repository(db, clock).GetActiveTrackedFlightsAsync();

        Assert.Empty(result);
    }

    /// <summary>
    /// The window is derived from <see cref="TimeProvider"/> on every call, not fixed at
    /// construction — advancing the fake clock past a previously-excluded flight's service date
    /// must bring it into range on the next call, from the very same repository instance.
    /// </summary>
    [Fact]
    public async Task GetActiveTrackedFlightsAsync_WindowMovesWithTheClock_NotFixedAtConstruction()
    {
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var travelerId = Guid.NewGuid();
        var repository = Repository(db, clock);

        TrackedFlight(db, travelerId, new DateOnly(2027, 3, 1), "TK1028");

        Assert.Empty(await repository.GetActiveTrackedFlightsAsync());

        clock.SetUtcNow(new DateTimeOffset(2027, 2, 27, 8, 0, 0, TimeSpan.Zero));

        Assert.Single(await repository.GetActiveTrackedFlightsAsync());
    }

    [Fact]
    public async Task GetActiveTrackedFlightsAsync_UpperBound_ReflectsAConfiguredStatusWindowFutureDays()
    {
        using var db = CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var travelerId = Guid.NewGuid();

        // With StatusWindowFutureDays = 1, the padded upper bound is today+2 (2026-09-03).
        TrackedFlight(db, travelerId, new DateOnly(2026, 9, 3), "TK1028");
        TrackedFlight(db, travelerId, new DateOnly(2026, 9, 4), "TK1029");

        var result = await Repository(db, clock, statusWindowFutureDays: 1).GetActiveTrackedFlightsAsync();

        Assert.Single(result);
        Assert.Equal("TK1028", result[0].FlightNumber);
    }
}
