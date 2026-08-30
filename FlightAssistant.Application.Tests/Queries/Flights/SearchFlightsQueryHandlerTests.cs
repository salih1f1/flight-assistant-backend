using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Contracts.Requests.Flights;
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

    [Fact]
    public async Task Handle_ReturnedRow_CarriesEveryFieldTrackFlightRequestNeeds()
    {
        // The whole point of task 4's reshape: a search result IS a TrackFlightRequest, field for
        // field, with no client-side transformation. Prove it explicitly here rather than leaving
        // it implied by the two records happening to look similar.
        var result = new FlightSearchResult(
            FlightNumber: "6E4120",
            ServiceDate: new DateOnly(2026, 9, 29),
            DepartureAirportIata: "SOF",
            ArrivalAirportIata: "IST",
            ScheduledDepartureUtc: new DateTime(2026, 9, 29, 6, 35, 0, DateTimeKind.Utc),
            ScheduledArrivalUtc: new DateTime(2026, 9, 29, 8, 5, 0, DateTimeKind.Utc));
        _search.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns([result]);

        var response = await _handler.Handle(
            new SearchFlightsQuery(null, null, result.ServiceDate, "6E4120"), CancellationToken.None);
        var row = Assert.Single(response);

        var trackRequest = new TrackFlightRequest(
            row.FlightNumber, row.ServiceDate, row.DepartureAirportIata, row.ArrivalAirportIata,
            row.ScheduledDepartureUtc, row.ScheduledArrivalUtc);

        Assert.Equal(result.FlightNumber, trackRequest.FlightNumber);
        Assert.Equal(result.ServiceDate, trackRequest.ServiceDate);
        Assert.Equal(result.DepartureAirportIata, trackRequest.DepartureAirportIata);
        Assert.Equal(result.ArrivalAirportIata, trackRequest.ArrivalAirportIata);
        Assert.Equal(result.ScheduledDepartureUtc, trackRequest.ScheduledDepartureUtc);
        Assert.Equal(result.ScheduledArrivalUtc, trackRequest.ScheduledArrivalUtc);
    }
}
