using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class UntrackFlightCommandHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly UntrackFlightCommandHandler _handler;

    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private static Flight PlannedFlight()
    {
        var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(10), Now.AddHours(18));
        flight.MarkSynced(Now.AddMinutes(-5));
        flight.ApplySyncPlan(FlightSyncMode.Live, Now.AddMinutes(2));
        return flight;
    }

    public UntrackFlightCommandHandlerTests()
    {
        _handler = new UntrackFlightCommandHandler(_flights);
    }

    [Fact]
    public async Task Handle_ExistingTracking_RemovesAndSaves()
    {
        var travelerId = Guid.NewGuid();
        var flightId = 1;
        var tf = TravelerFlight.Create(travelerId, flightId);

        _flights.GetTravelerFlightAsync(travelerId, flightId, Arg.Any<CancellationToken>())
            .Returns(tf);
        _flights.GetTrackingsByFlightIdAsync(flightId, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tf });

        await _handler.Handle(new UntrackFlightCommand(travelerId, flightId), CancellationToken.None);

        await _flights.Received(1).RemoveTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotTracking_CompletesGracefully()
    {
        _flights.GetTravelerFlightAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        await _handler.Handle(new UntrackFlightCommand(Guid.NewGuid(), 1), CancellationToken.None);

        await _flights.DidNotReceive().RemoveTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LastTracker_ClearsThePlan_KeepsLastSynced()
    {
        var travelerId = Guid.NewGuid();
        var tf = TravelerFlight.Create(travelerId, 1);
        var flight = PlannedFlight();
        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>()).Returns(tf);
        // Still returned: the removal isn't saved yet.
        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TravelerFlight> { tf });
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);

        await _handler.Handle(new UntrackFlightCommand(travelerId, 1), CancellationToken.None);

        Assert.Null(flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
        Assert.Equal(Now.AddMinutes(-5), flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtherTrackersRemain_KeepsThePlan()
    {
        var travelerId = Guid.NewGuid();
        var tf = TravelerFlight.Create(travelerId, 1);
        var flight = PlannedFlight();
        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>()).Returns(tf);
        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tf, TravelerFlight.Create(Guid.NewGuid(), 1) });
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);

        await _handler.Handle(new UntrackFlightCommand(travelerId, 1), CancellationToken.None);

        Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(2), flight.NextSyncDueUtc);
    }
}
