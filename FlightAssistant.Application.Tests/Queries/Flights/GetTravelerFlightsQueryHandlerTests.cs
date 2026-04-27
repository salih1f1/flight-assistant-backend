using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Domain.Flights.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Flights;

public class GetTravelerFlightsQueryHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IRefDataRepository _refData = Substitute.For<IRefDataRepository>();
    private readonly GetTravelerFlightsQueryHandler _handler;

    public GetTravelerFlightsQueryHandlerTests()
    {
        _handler = new GetTravelerFlightsQueryHandler(_flights, _refData);
    }

    [Fact]
    public async Task ReturnsPagedResults_WithCorrectMappings()
    {
        var travelerId = Guid.NewGuid();
        var flight = Flight.Create(1, "BA123", 10, 20,
            DateOnly.FromDateTime(DateTime.Today),
            DateTime.UtcNow, DateTime.UtcNow.AddHours(8));

        var tracking = TravelerFlight.Create(travelerId, flight.Id);

        _flights.GetTravelerFlightsAsync(travelerId, 1, 50, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<(TravelerFlight, Flight)>)[(tracking, flight)], 1));

        _refData.GetAirlineIataByIdAsync(1, Arg.Any<CancellationToken>()).Returns("BA");
        _refData.GetAirportIataByIdAsync(10, Arg.Any<CancellationToken>()).Returns("LHR");
        _refData.GetAirportIataByIdAsync(20, Arg.Any<CancellationToken>()).Returns("JFK");

        var query = new GetTravelerFlightsQuery(travelerId, new PagedRequest(1, 50));
        var result = await _handler.Handle(query, CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("BA123", item.FlightNumber);
        Assert.Equal("BA", item.AirlineIata);
        Assert.Equal("LHR", item.DepartureIata);
        Assert.Equal("JFK", item.ArrivalIata);
        Assert.True(item.Notify); // TravelerFlight defaults to Notify=true
    }

    [Fact]
    public async Task EmptyList_ReturnsEmptyPage()
    {
        var travelerId = Guid.NewGuid();
        _flights.GetTravelerFlightsAsync(travelerId, 1, 50, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<(TravelerFlight, Flight)>)[], 0));

        var query = new GetTravelerFlightsQuery(travelerId, new PagedRequest(1, 50));
        var result = await _handler.Handle(query, CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }
}
