using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.ReferenceData;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Events;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// #410 fix round, Fix 4: pins that re-tracking an <b>already-tracked</b> flight actually dispatches
/// <see cref="FlightTracked"/> through the real <see cref="DomainEventDispatchInterceptor"/> -- the
/// path R1 exists for, since a repeat track is exactly the case whose reference rows are most likely
/// to have gone stale since the last check. Nothing else in the suite proves this end to end:
/// <c>TrackFlightCommandHandlerTests</c> (Application.Tests) mocks <see cref="IFlightRepository"/>,
/// so it proves the command calls <c>SaveChangesAsync</c> and that the event lands on the aggregate --
/// not that a zero-change save actually reaches <c>SavedChangesAsync</c>, nor that the repeat-path
/// <see cref="Flight"/> is EF-tracked at all. <c>FlightAssistant.API.Tests</c>'s factory strips every
/// EF interceptor (#319), so an API-level test could never observe dispatch either.
///
/// <para>
/// <b>The mechanism this pins.</b> <see cref="FlightRepository.GetByFlightNumberAndDateAsync"/> --
/// the query <c>GetOrCreateFlightAsync</c> uses to find the already-tracked flight, and the one that
/// also backs the read-only <c>AlreadyTracksAsync</c> -- has no <c>AsNoTracking()</c>, so the
/// <see cref="Flight"/> it returns is EF-tracked. <c>DomainEventDispatchInterceptor</c> harvests
/// events from every tracked <c>IHasDomainEvents</c> entry regardless of whether anything on it
/// actually changed, so the handler's unconditional <c>SaveChangesAsync</c> call still dispatches
/// even though nothing is dirty. A future ".AsNoTracking()" on that method -- a plausible-looking
/// optimisation, since it is also used for a read-only check -- would detach the returned
/// <see cref="Flight"/>, and the interceptor would never see its event again: the trigger would
/// silently stop firing on the repeat-track path while every other existing test (none of which
/// re-track through a real, intercepted context) stayed green.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class DomainEventDispatchInterceptorTrackFlightPostgresTests : PostgresTestBase
{
    private readonly PostgresContainerFixture _fixture;

    public DomainEventDispatchInterceptorTrackFlightPostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    private sealed class SyncOptions : IFlightSyncOptions
    {
        public TimeSpan NearDepartureInterval => TimeSpan.FromMinutes(3);
        public TimeSpan Interval => TimeSpan.FromMinutes(7);
        public TimeSpan ScheduleCheckInterval => TimeSpan.FromHours(6);
        public TimeSpan FastWindowBeforeDeparture => TimeSpan.FromHours(3);
        public TimeSpan StopAfterArrival => TimeSpan.FromMinutes(20);
        public TimeSpan StopWithoutArrivalAfter => TimeSpan.FromHours(12);
        public TimeSpan ManualSyncCooldown => TimeSpan.FromMinutes(4);
        public int MaxActiveTrackedFlightsPerTraveler => 3;
    }

    private sealed class WindowOptions : IFlightStatusWindowOptions
    {
        public int StatusWindowFutureDays => 1;
    }

    /// <summary>A context wired with the real <see cref="DomainEventDispatchInterceptor"/> over a
    /// substitute <see cref="IPublisher"/>, mirroring <c>AdminAuditInterceptorPostgresTests</c>'s
    /// own <c>CreateAuditedDbContext</c> -- <see cref="PostgresTestBase.CreateDbContext"/> carries no
    /// interceptor at all, so a test that needs one builds its own options here instead.</summary>
    private FlightAssistantDbContext CreateInterceptedDbContext(IPublisher publisher) =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(new DomainEventDispatchInterceptor(publisher))
            .Options);

    [Fact]
    public async Task Handle_RepeatTrackOfAnAlreadyTrackedFlight_StillDispatchesFlightTracked()
    {
        var travelerId = Guid.NewGuid();
        var serviceDate = new DateOnly(2026, 9, 20);
        var scheduledDeparture = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);
        var scheduledArrival = scheduledDeparture.AddHours(8);
        var clock = new FakeTimeProvider(new DateTimeOffset(scheduledDeparture.AddDays(-1)));

        int airlineId, departureAirportId, arrivalAirportId, flightId;

        // Seed a flight the traveler already tracks -- real Postgres FK constraints need real
        // Country/Airline/Airport/Traveler rows behind it, mirroring FlightRepositoryTests'
        // EnsureReferenceData/EnsureTraveler helpers.
        await using (var seed = CreateDbContext())
        {
            seed.Countries.Add(Country.Create("US", "United States"));
            var airline = Airline.Create("TK", "THY", "Test Airline", "US");
            var departure = Airport.Create("JFK", "KJFK", "JFK Airport", "New York", "US");
            var arrival = Airport.Create("LAX", "KLAX", "LAX Airport", "Los Angeles", "US");
            seed.Airlines.Add(airline);
            seed.Airports.Add(departure);
            seed.Airports.Add(arrival);
            seed.Travelers.Add(Traveler.Register(travelerId, $"{travelerId:N}@test.local"));
            await seed.SaveChangesAsync();

            var flight = Flight.Create(airline.Id, "TK001", departure.Id, arrival.Id,
                serviceDate, scheduledDeparture, scheduledArrival);
            seed.Flights.Add(flight);
            await seed.SaveChangesAsync();

            seed.TravelerFlights.Add(TravelerFlight.Create(travelerId, flight.Id));
            await seed.SaveChangesAsync();

            airlineId = airline.Id;
            departureAirportId = departure.Id;
            arrivalAirportId = arrival.Id;
            flightId = flight.Id;
        }

        var publisher = Substitute.For<IPublisher>();
        await using var db = CreateInterceptedDbContext(publisher);
        // Every reference row is seeded above, so the provisioner is never consulted (#465) --
        // this test pins event dispatch on the repeat-track path, not provisioning.
        var handler = new TrackFlightCommandHandler(
            new FlightRepository(db, clock),
            new RefDataRepository(db),
            Substitute.For<IReferenceDataProvisioner>(),
            new SyncOptions(),
            new WindowOptions(),
            clock);

        // The same traveler, the same flight -- the repeat-track path, not the first-ever track.
        var command = new TrackFlightCommand(
            travelerId, "TK001", serviceDate, "JFK", "LAX", scheduledDeparture, scheduledArrival);

        var returnedFlightId = await handler.Handle(command, CancellationToken.None);

        Assert.Equal(flightId, returnedFlightId);
        await publisher.Received(1).Publish(
            Arg.Is<FlightTracked>(e =>
                e.FlightId == flightId &&
                e.AirlineId == airlineId &&
                e.DepartureAirportId == departureAirportId &&
                e.ArrivalAirportId == arrivalAirportId),
            Arg.Any<CancellationToken>());
    }
}
