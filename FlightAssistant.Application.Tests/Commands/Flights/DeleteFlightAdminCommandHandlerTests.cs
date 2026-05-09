using FlightAssistant.Application.Commands.Admin.Flights;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Events;
using MediatR;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class DeleteFlightAdminCommandHandlerTests
{
    private readonly IAdminFlightRepository _adminRepo = Substitute.For<IAdminFlightRepository>();
    private readonly IFlightRepository _flightRepo = Substitute.For<IFlightRepository>();
    private readonly IPublisher _publisher = Substitute.For<IPublisher>();
    private readonly DeleteFlightAdminCommandHandler _handler;

    private static Flight SampleFlight() => Flight.Create(
        1, "BA123", 10, 20,
        DateOnly.FromDateTime(DateTime.Today),
        DateTime.UtcNow, DateTime.UtcNow.AddHours(8));

    public DeleteFlightAdminCommandHandlerTests()
    {
        _handler = new DeleteFlightAdminCommandHandler(_adminRepo, _flightRepo, _publisher);
    }

    [Fact]
    public async Task Handle_FlightNotFound_DoesNothingAndDoesNotPublish()
    {
        _adminRepo.GetByIntIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((Flight?)null);

        await _handler.Handle(new DeleteFlightAdminCommand(99), default);

        await _adminRepo.DidNotReceive().DeleteAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>());
        await _publisher.DidNotReceive().Publish(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FlightWithNotifyTrackers_DeletesAndPublishesEvent()
    {
        var flight = SampleFlight();
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);

        _adminRepo.GetByIntIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);
        _flightRepo.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });

        await _handler.Handle(new DeleteFlightAdminCommand(1), default);

        await _adminRepo.Received(1).DeleteAsync(flight, Arg.Any<CancellationToken>());
        await _adminRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _publisher.Received(1).Publish(
            Arg.Is<FlightDeleted>(e =>
                e.FlightNumber == "BA123" &&
                e.TrackerTravelerIds.Contains(travelerId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FlightWithNoNotifyTrackers_DeletesWithoutPublishing()
    {
        var flight = SampleFlight();
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(false);

        _adminRepo.GetByIntIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);
        _flightRepo.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });

        await _handler.Handle(new DeleteFlightAdminCommand(1), default);

        await _adminRepo.Received(1).DeleteAsync(flight, Arg.Any<CancellationToken>());
        await _publisher.DidNotReceive().Publish(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FlightWithNoTrackers_DeletesWithoutPublishing()
    {
        var flight = SampleFlight();

        _adminRepo.GetByIntIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);
        _flightRepo.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight>());

        await _handler.Handle(new DeleteFlightAdminCommand(1), default);

        await _adminRepo.Received(1).DeleteAsync(flight, Arg.Any<CancellationToken>());
        await _publisher.DidNotReceive().Publish(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }
}
