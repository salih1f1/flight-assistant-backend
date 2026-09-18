using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.RefData;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Queries.Admin.RefData;

public class GetAdminAllianceDetailQueryHandlerTests
{
    private readonly IAllianceRepository _alliances = Substitute.For<IAllianceRepository>();
    private readonly IAirlineRepository _airlines = Substitute.For<IAirlineRepository>();
    private readonly GetAdminAllianceDetailQueryHandler _handler;

    public GetAdminAllianceDetailQueryHandlerTests()
        => _handler = new GetAdminAllianceDetailQueryHandler(_alliances, _airlines);

    private void GivenAlliance(int id, string code, string name) =>
        _alliances.FindByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Alliance.Create(code, name));

    [Fact]
    public async Task Handle_AllianceNotFound_ReturnsNull()
    {
        _alliances.FindByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        var result = await _handler.Handle(new GetAdminAllianceDetailQuery(99), CancellationToken.None);

        Assert.Null(result);
        // Short-circuits before asking for airlines: a wasted round trip for a 404 is the query-count
        // creep #322's "no N+1" rule guards against, and this handler has only two calls to begin with.
        await _airlines.DidNotReceive()
            .GetByAllianceCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReturnsTheAllianceWithItsAirlines()
    {
        GivenAlliance(3, "OW", "oneworld");
        _airlines.GetByAllianceCodeAsync("OW", Arg.Any<CancellationToken>()).Returns(
        [
            Airline.Create("AA", "AAL", "American Airlines", "US", "OW"),
            Airline.Create("BA", "BAW", "British Airways", "GB", "OW"),
        ]);

        var result = await _handler.Handle(new GetAdminAllianceDetailQuery(3), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("OW", result.Code);
        Assert.Equal("oneworld", result.Name);
        Assert.Equal(["American Airlines", "British Airways"], result.Airlines.Select(a => a.Name));
        var first = result.Airlines[0];
        Assert.Equal("AA", first.Iata);
        Assert.Equal("AAL", first.Icao);
        Assert.Equal("US", first.CountryCode);
    }

    /// <summary>
    /// The airlines are looked up by the alliance's <c>Code</c>, never its id: <c>Code</c> is the
    /// column <c>airlines.AllianceCode</c> references by foreign key, and the two are different
    /// numbers/strings entirely.
    /// </summary>
    [Fact]
    public async Task Handle_LooksTheAirlinesUpByTheAlliancesCode()
    {
        GivenAlliance(3, "OW", "oneworld");

        await _handler.Handle(new GetAdminAllianceDetailQuery(3), CancellationToken.None);

        await _airlines.Received(1).GetByAllianceCodeAsync("OW", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An alliance nobody belongs to is a normal answer, not an error — the same call
    /// <c>GET /admin/verification-requests</c> makes for an entity with no checks yet.
    /// </summary>
    [Fact]
    public async Task Handle_AllianceWithNoAirlines_ReturnsAnEmptyListNotNull()
    {
        GivenAlliance(4, "ST", "SkyTeam");
        _airlines.GetByAllianceCodeAsync("ST", Arg.Any<CancellationToken>()).Returns([]);

        var result = await _handler.Handle(new GetAdminAllianceDetailQuery(4), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Airlines);
    }
}
