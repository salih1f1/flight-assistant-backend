using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class ToggleFlightNotificationCommandHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly ToggleFlightNotificationCommandHandler _handler;

    public ToggleFlightNotificationCommandHandlerTests()
    {
        _handler = new ToggleFlightNotificationCommandHandler(_flights);
    }

    [Fact]
    public async Task Handle_FlightFound_TogglesNotify()
    {
        var travelerId = Guid.NewGuid();
        var flightId = 1;
        var tf = TravelerFlight.Create(travelerId, flightId);

        _flights.GetTravelerFlightAsync(travelerId, flightId, Arg.Any<CancellationToken>())
            .Returns(tf);

        await _handler.Handle(new ToggleFlightNotificationCommand(travelerId, flightId, false), CancellationToken.None);

        Assert.False(tf.Notify);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FlightNotFound_ThrowsDomainException()
    {
        _flights.GetTravelerFlightAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new ToggleFlightNotificationCommand(Guid.NewGuid(), 1, true), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
    }
}
