using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class UntrackFlightCommandHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly UntrackFlightCommandHandler _handler;

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
}
