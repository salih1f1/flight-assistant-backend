using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Flights;
using FlightAssistant.Domain.Flights.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Queries.Admin.Flights;

/// <summary>
/// <see cref="FlightStatusEvent.Status"/> is a private-setter navigation property that only EF's
/// own materializer ever populates (see <see cref="FlightStatus"/> — no
/// factory method, no public constructor; nothing in this codebase constructs one directly, exactly
/// because of that). <see cref="GetAdminFlightDetailQueryHandler"/> reads <c>e.Status.Code</c>/
/// <c>e.Status.Name</c> for every event it maps, so a mocked, hand-built
/// <see cref="FlightStatusEvent"/> can never safely stand in for a real one here without throwing a
/// <see cref="NullReferenceException"/>. This class therefore only covers the paths that never
/// dereference an event's <c>Status</c> — the empty-history path, the not-found short-circuit, and
/// that the configured cap is what actually gets sent to the repository. The per-event mapping
/// itself (status code/name, the provider's raw status extracted from <c>RawPayloadJson</c>, and
/// that <c>CurrentStatus</c> really is the newest-first list's first element) is proved instead
/// against real PostgreSQL in
/// <c>FlightAssistant.Infrastructure.Tests.Persistance.Repositories.AdminDrilldownRepositoryTests</c>,
/// where <c>Include(e =&gt; e.Status)</c> populates it for real.
/// </summary>
public class GetAdminFlightDetailQueryHandlerTests
{
    private readonly IAdminFlightRepository _adminFlights = Substitute.For<IAdminFlightRepository>();
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly GetAdminFlightDetailQueryHandler _handler;

    public GetAdminFlightDetailQueryHandlerTests()
        => _handler = new GetAdminFlightDetailQueryHandler(_adminFlights, _flights);

    [Fact]
    public async Task Handle_FlightNotFound_ReturnsNull()
    {
        _adminFlights.GetDetailAsync(1, Arg.Any<CancellationToken>()).ReturnsNull();

        var result = await _handler.Handle(new GetAdminFlightDetailQuery(1), CancellationToken.None);

        Assert.Null(result);
        await _flights.DidNotReceive().GetRecentFlightStatusEventsAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FlightFoundWithNoStatusEvents_CurrentStatusIsNull_HistoryIsEmpty()
    {
        var flight = Flight.Create(1, "BA123", 10, 20,
            new DateOnly(2026, 9, 10), DateTime.UtcNow, DateTime.UtcNow.AddHours(2));
        _adminFlights.GetDetailAsync(1, Arg.Any<CancellationToken>()).Returns(flight);
        _flights.GetRecentFlightStatusEventsAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FlightStatusEvent>)[]);

        var result = await _handler.Handle(new GetAdminFlightDetailQuery(1), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.CurrentStatus);
        Assert.Empty(result.StatusHistory);
        Assert.False(result.StatusHistoryTruncated);
        Assert.Equal("BA123", result.FlightNumber);
    }

    /// <summary>
    /// The cap (#322's "page it, or cap it with a documented limit") is a constant threaded through
    /// to the repository call, not merely applied after the fact — this is the one thing about the
    /// cap this mock-based test can prove; that the repository's <c>LIMIT</c> is what actually
    /// bounds the row count is proved separately against real PostgreSQL.
    /// </summary>
    [Fact]
    public async Task Handle_RequestsExactlyTheConfiguredMaxStatusHistoryFromTheRepository()
    {
        var flight = Flight.Create(1, "BA123", 10, 20,
            new DateOnly(2026, 9, 10), DateTime.UtcNow, DateTime.UtcNow.AddHours(2));
        _adminFlights.GetDetailAsync(5, Arg.Any<CancellationToken>()).Returns(flight);
        _flights.GetRecentFlightStatusEventsAsync(5, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FlightStatusEvent>)[]);

        await _handler.Handle(new GetAdminFlightDetailQuery(5), CancellationToken.None);

        await _flights.Received(1).GetRecentFlightStatusEventsAsync(
            5, GetAdminFlightDetailQueryHandler.MaxStatusHistory, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MapsEveryFlightField()
    {
        var flight = Flight.Create(
            airlineId: 3, flightNumber: "TK99", departureAirportId: 11, arrivalAirportId: 22,
            serviceDate: new DateOnly(2026, 10, 1),
            scheduledDepartureUtc: new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            scheduledArrivalUtc: new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            externalId: "ext-1",
            equipmentAircraftIcao: "A320");
        _adminFlights.GetDetailAsync(9, Arg.Any<CancellationToken>()).Returns(flight);
        _flights.GetRecentFlightStatusEventsAsync(9, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FlightStatusEvent>)[]);

        var result = await _handler.Handle(new GetAdminFlightDetailQuery(9), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.AirlineId);
        Assert.Equal("TK99", result.FlightNumber);
        Assert.Equal(11, result.DepartureAirportId);
        Assert.Equal(22, result.ArrivalAirportId);
        Assert.Equal(new DateOnly(2026, 10, 1), result.ServiceDate);
        Assert.Equal("ext-1", result.ExternalId);
        Assert.Equal("A320", result.EquipmentAircraftIcao);
    }
}
