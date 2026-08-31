using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Flights;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Flights;

public class SearchFlightsQueryHandlerTests
{
    private readonly IFlightSearchProvider _search = Substitute.For<IFlightSearchProvider>();
    private readonly SearchFlightsQueryHandler _handler;

    public SearchFlightsQueryHandlerTests()
    {
        _handler = new SearchFlightsQueryHandler(_search);
    }

    [Fact]
    public async Task Handle_UppercasesRouteAndFlightNumber_BeforeCallingTheProvider()
    {
        var query = new SearchFlightsQuery("lhr", "jfk", new DateOnly(2026, 9, 29), "ba123");
        _search.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _handler.Handle(query, CancellationToken.None);

        await _search.Received(1).SearchAsync(
            Arg.Is<FlightSearchCriteria>(c =>
                c.DepartureIata == "LHR" && c.ArrivalIata == "JFK" && c.FlightNumber == "BA123"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_PassesIncludeCodeshares_ThroughToTheCriteria(bool includeCodeshares)
    {
        var query = new SearchFlightsQuery("LHR", "JFK", new DateOnly(2026, 9, 29), null, includeCodeshares);
        _search.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _handler.Handle(query, CancellationToken.None);

        await _search.Received(1).SearchAsync(
            Arg.Is<FlightSearchCriteria>(c => c.IncludeCodeshares == includeCodeshares),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReturnsWhatTheProviderReturns_Unmodified()
    {
        var results = new List<FlightSearchResult>
        {
            new("BA123", new DateOnly(2026, 9, 29), "LHR", "JFK",
                new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 29, 17, 0, 0, DateTimeKind.Utc))
        };
        _search.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(results);

        var query = new SearchFlightsQuery("LHR", "JFK", new DateOnly(2026, 9, 29));
        var response = await _handler.Handle(query, CancellationToken.None);

        Assert.Same(results, response);
    }

    // Deliberately NOT here: a test that copies a FlightSearchResult's six fields into a
    // TrackFlightRequest and asserts each copy equals its source. That is a tautology — it can only
    // fail at compile time, and would pass unchanged with the handler deleted.
    // FlightEndpointTests.SearchFlights_ReturnedRow_DeserialisesIntoAValidTrackFlightRequest does
    // the real job, at the HTTP/JSON boundary where the shapes could actually diverge.
}
