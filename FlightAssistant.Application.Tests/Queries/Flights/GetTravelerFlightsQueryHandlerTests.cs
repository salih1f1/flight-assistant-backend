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

        _refData.GetAirlineIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [1] = "BA" });
        _refData.GetAirportIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [10] = "LHR", [20] = "JFK" });

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
    public async Task MultipleRows_CallsBatchMapsOnceRegardlessOfRowCount()
    {
        var travelerId = Guid.NewGuid();

        var flight1 = Flight.Create(1, "BA123", 10, 20,
            DateOnly.FromDateTime(DateTime.Today),
            DateTime.UtcNow, DateTime.UtcNow.AddHours(8));
        var flight2 = Flight.Create(1, "BA456", 20, 30,
            DateOnly.FromDateTime(DateTime.Today),
            DateTime.UtcNow, DateTime.UtcNow.AddHours(8));
        var flight3 = Flight.Create(2, "AF789", 10, 30,
            DateOnly.FromDateTime(DateTime.Today),
            DateTime.UtcNow, DateTime.UtcNow.AddHours(8));

        var tracking1 = TravelerFlight.Create(travelerId, flight1.Id);
        var tracking2 = TravelerFlight.Create(travelerId, flight2.Id);
        var tracking3 = TravelerFlight.Create(travelerId, flight3.Id);

        _flights.GetTravelerFlightsAsync(travelerId, 1, 50, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<(TravelerFlight, Flight)>)
                [(tracking1, flight1), (tracking2, flight2), (tracking3, flight3)], 3));

        _refData.GetAirlineIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [1] = "BA", [2] = "AF" });
        _refData.GetAirportIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [10] = "LHR", [20] = "JFK", [30] = "CDG" });

        var query = new GetTravelerFlightsQuery(travelerId, new PagedRequest(1, 50));
        var result = await _handler.Handle(query, CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Items.Count);

        await _refData.Received(1).GetAirlineIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
        await _refData.Received(1).GetAirportIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());

        await _refData.DidNotReceive().GetAirlineIataByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _refData.DidNotReceive().GetAirportIataByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyList_ReturnsEmptyPage()
    {
        var travelerId = Guid.NewGuid();
        _flights.GetTravelerFlightsAsync(travelerId, 1, 50, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<(TravelerFlight, Flight)>)[], 0));

        _refData.GetAirlineIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());
        _refData.GetAirportIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());

        var query = new GetTravelerFlightsQuery(travelerId, new PagedRequest(1, 50));
        var result = await _handler.Handle(query, CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }
}
