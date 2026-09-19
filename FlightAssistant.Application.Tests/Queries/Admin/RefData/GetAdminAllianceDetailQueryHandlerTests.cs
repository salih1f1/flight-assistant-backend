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
    private readonly ILoyaltyRepository _loyalty = Substitute.For<ILoyaltyRepository>();
    private readonly GetAdminAllianceDetailQueryHandler _handler;

    public GetAdminAllianceDetailQueryHandlerTests()
    {
        _handler = new GetAdminAllianceDetailQueryHandler(_alliances, _airlines, _loyalty);
        // The default an unconfigured substitute returns for a dictionary is null, which no caller
        // would ever get from the real repository.
        _loyalty.GetProgramNamesByAirlineIdsAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());
    }

    private void GivenAlliance(int id, string code, string name) =>
        _alliances.FindByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Alliance.Create(code, name));

    /// <summary>
    /// Airline.Create assigns no id — the database does — so the ids the handler passes to the
    /// loyalty lookup are all 0 unless the test sets them. Reflection is the only way in: Id is
    /// private-set by design, and an int is not something the domain should expose a setter for
    /// just so a test can spell one.
    /// </summary>
    private static Airline WithId(int id, Airline airline)
    {
        typeof(Airline).GetProperty(nameof(Airline.Id))!.SetValue(airline, id);
        return airline;
    }

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
    /// <summary>
    /// The airline's own loyalty program is the fact the panel's alliance list is read for
    /// (#495): the alliance's members, each with the programme a traveller would collect on.
    /// </summary>
    [Fact]
    public async Task Handle_FillsEachAirlinesLoyaltyProgramName()
    {
        GivenAlliance(3, "OW", "oneworld");
        _airlines.GetByAllianceCodeAsync("OW", Arg.Any<CancellationToken>()).Returns(
        [
            WithId(11, Airline.Create("AA", "AAL", "American Airlines", "US", "OW")),
            WithId(12, Airline.Create("BA", "BAW", "British Airways", "GB", "OW")),
        ]);
        _loyalty.GetProgramNamesByAirlineIdsAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [11] = "AAdvantage", [12] = "Executive Club" });

        var result = await _handler.Handle(new GetAdminAllianceDetailQuery(3), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(
            ["AAdvantage", "Executive Club"],
            result.Airlines.Select(a => a.LoyaltyProgramName));
    }

    /// <summary>
    /// The lookup is asked for the member airlines' ids and nothing else — one call for the whole
    /// list, not the per-airline <c>SearchProgramsByAirlineIdAsync</c> in a loop, which is the N+1
    /// the other drill-downs are written to avoid.
    /// </summary>
    [Fact]
    public async Task Handle_LooksTheProgramsUpForEveryMemberAirlineInOneCall()
    {
        GivenAlliance(3, "OW", "oneworld");
        _airlines.GetByAllianceCodeAsync("OW", Arg.Any<CancellationToken>()).Returns(
        [
            WithId(11, Airline.Create("AA", "AAL", "American Airlines", "US", "OW")),
            WithId(12, Airline.Create("BA", "BAW", "British Airways", "GB", "OW")),
        ]);

        await _handler.Handle(new GetAdminAllianceDetailQuery(3), CancellationToken.None);

        await _loyalty.Received(1).GetProgramNamesByAirlineIdsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { 11, 12 })),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Most airlines have no programme of their own. The row still belongs on the list — it is the
    /// alliance's members, not the alliance's members that happen to run a programme — so the
    /// missing name is null, not a dropped airline.
    /// </summary>
    [Fact]
    public async Task Handle_AirlineWithNoLoyaltyProgram_KeepsTheAirlineWithANullProgramName()
    {
        GivenAlliance(3, "OW", "oneworld");
        _airlines.GetByAllianceCodeAsync("OW", Arg.Any<CancellationToken>()).Returns(
        [
            WithId(11, Airline.Create("AA", "AAL", "American Airlines", "US", "OW")),
            WithId(12, Airline.Create("BA", "BAW", "British Airways", "GB", "OW")),
        ]);
        _loyalty.GetProgramNamesByAirlineIdsAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [11] = "AAdvantage" });

        var result = await _handler.Handle(new GetAdminAllianceDetailQuery(3), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(["American Airlines", "British Airways"], result.Airlines.Select(a => a.Name));
        Assert.Null(result.Airlines.Single(a => a.Name == "British Airways").LoyaltyProgramName);
    }

    [Fact]
    public async Task Handle_AllianceWithNoAirlines_ReturnsAnEmptyListNotNull()
    {
        GivenAlliance(4, "ST", "SkyTeam");
        _airlines.GetByAllianceCodeAsync("ST", Arg.Any<CancellationToken>()).Returns([]);

        var result = await _handler.Handle(new GetAdminAllianceDetailQuery(4), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Airlines);
        // Nothing to look programs up for: an empty IN (...) is a round trip that can only come
        // back empty.
        await _loyalty.DidNotReceive().GetProgramNamesByAirlineIdsAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }
}
