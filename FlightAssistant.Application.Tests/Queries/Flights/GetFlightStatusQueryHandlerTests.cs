using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Queries.Flights;

public class GetFlightStatusQueryHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly GetFlightStatusQueryHandler _handler;

    public GetFlightStatusQueryHandlerTests()
        => _handler = new GetFlightStatusQueryHandler(_flights);

    [Fact]
    public async Task Handle_TravelerNotTrackingFlight_ThrowsDomainException403()
    {
        var travelerId = Guid.NewGuid();
        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>())
            .ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(new GetFlightStatusQuery(travelerId, 1), CancellationToken.None));

        Assert.Equal(403, ex.StatusCode);
        await _flights.DidNotReceive().GetFlightStatusEventsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TravelerIsTracking_ReturnsEvents()
    {
        var travelerId = Guid.NewGuid();
        var flight = Flight.Create(1, "BA123", 10, 20,
            DateOnly.FromDateTime(DateTime.Today),
            DateTime.UtcNow, DateTime.UtcNow.AddHours(8));
        var tracking = TravelerFlight.Create(travelerId, flight.Id);

        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>())
            .Returns(tracking);
        _flights.GetFlightStatusEventsAsync(1, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FlightStatusEvent>)[]);

        var result = await _handler.Handle(new GetFlightStatusQuery(travelerId, 1), CancellationToken.None);

        Assert.Empty(result);
    }
}
