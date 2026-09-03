using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Travelers;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Queries.Admin.Travelers;

public class GetAdminTravelerDetailQueryHandlerTests
{
    private readonly IAdminTravelerRepository _travelers = Substitute.For<IAdminTravelerRepository>();
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly ILoyaltyRepository _loyalty = Substitute.For<ILoyaltyRepository>();
    private readonly GetAdminTravelerDetailQueryHandler _handler;

    public GetAdminTravelerDetailQueryHandlerTests()
        => _handler = new GetAdminTravelerDetailQueryHandler(_travelers, _flights, _loyalty);

    [Fact]
    public async Task Handle_TravelerNotFound_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _travelers.GetDetailAsync(id, Arg.Any<CancellationToken>()).ReturnsNull();

        var result = await _handler.Handle(new GetAdminTravelerDetailQuery(id), CancellationToken.None);

        Assert.Null(result);
        // Short-circuits before ever asking about flights/memberships — one wasted repository
        // round trip for a 404 is exactly the kind of query-count creep #322's "no N+1" rule guards
        // against, even outside the "many related rows" case.
        await _flights.DidNotReceive().GetRecentTravelerFlightsAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _loyalty.DidNotReceive().GetAllMembershipsForTravelerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The security-critical assertion for this handler: <see cref="Traveler.Email"/>,
    /// <see cref="DeviceToken.Token"/> and <see cref="TravelerLoyaltyMembership.AccountNumber"/> are
    /// all registered in <c>SensitiveProperties</c> and must never leave this handler unmasked — and
    /// each is checked alongside an unmasked control field from the very same nested object
    /// (<c>DisplayName</c>, <c>Platform</c>, <c>TierId</c>) so a passing assertion is proven to
    /// discriminate rather than passing because the whole response happened to be empty or because
    /// masking silently redacted everything, control fields included.
    /// </summary>
    [Fact]
    public async Task Handle_TravelerFound_MasksEmailTokenAndAccountNumber_ButLeavesDisplayNamePlatformAndTierIdUnmasked()
    {
        var travelerId = Guid.NewGuid();
        var rawEmail = "very-secret@example.com";
        var traveler = Traveler.Register(travelerId, rawEmail, "Findable Traveler");
        var rawToken = "very-secret-device-token";
        traveler.AddDeviceToken("ios", rawToken);

        var flight = Flight.Create(1, "BA123", 10, 20,
            new DateOnly(2026, 9, 10), DateTime.UtcNow, DateTime.UtcNow.AddHours(2));
        var tracking = TravelerFlight.Create(travelerId, flight.Id);

        var rawAccountNumber = "ACCT-VERY-SECRET";
        var membership = TravelerLoyaltyMembership.Create(travelerId, programId: 7, accountNumber: rawAccountNumber, tierId: 3);

        _travelers.GetDetailAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);
        _flights.GetRecentTravelerFlightsAsync(travelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<(TravelerFlight, Flight)>)[(tracking, flight)]);
        _loyalty.GetAllMembershipsForTravelerAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TravelerLoyaltyMembership>)[membership]);

        var result = await _handler.Handle(new GetAdminTravelerDetailQuery(travelerId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("<redacted>", result!.Email);
        Assert.NotEqual(rawEmail, result.Email);
        Assert.Equal("Findable Traveler", result.DisplayName); // control: unmasked

        var token = Assert.Single(result.DeviceTokens);
        Assert.Equal("<redacted>", token.Token);
        Assert.NotEqual(rawToken, token.Token);
        Assert.Equal("ios", token.Platform); // control: unmasked

        var trackedFlight = Assert.Single(result.TrackedFlights);
        Assert.Equal("BA123", trackedFlight.FlightNumber);
        // M6: the join already loads the whole Flight row, so both airport ids ride along for free.
        Assert.Equal(10, trackedFlight.DepartureAirportId);
        Assert.Equal(20, trackedFlight.ArrivalAirportId);
        Assert.False(result.TrackedFlightsTruncated); // one row, nowhere near the cap

        var membershipResult = Assert.Single(result.LoyaltyMemberships);
        Assert.Equal("<redacted>", membershipResult.AccountNumber);
        Assert.NotEqual(rawAccountNumber, membershipResult.AccountNumber);
        Assert.Equal(3, membershipResult.TierId); // control: unmasked
    }

    [Fact]
    public async Task Handle_TravelerWithNoRelationships_ReturnsEmptyListsNotNull()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register(travelerId, "solo@example.com");

        _travelers.GetDetailAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);
        _flights.GetRecentTravelerFlightsAsync(travelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<(TravelerFlight, Flight)>)[]);
        _loyalty.GetAllMembershipsForTravelerAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TravelerLoyaltyMembership>)[]);

        var result = await _handler.Handle(new GetAdminTravelerDetailQuery(travelerId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.DeviceTokens);
        Assert.Empty(result.TrackedFlights);
        Assert.False(result.TrackedFlightsTruncated);
        Assert.Empty(result.LoyaltyMemberships);
    }

    private List<(TravelerFlight, Flight)> TrackedFlights(Guid travelerId, int count) =>
        Enumerable.Range(0, count)
            .Select(i =>
            {
                var flight = Flight.Create(1, $"TK{i}", 10, 20,
                    new DateOnly(2026, 9, 10).AddDays(i), DateTime.UtcNow, DateTime.UtcNow.AddHours(2));
                return (TravelerFlight.Create(travelerId, flight.Id), flight);
            })
            .ToList();

    /// <summary>
    /// #322 fix round, M5: more rows exist than the cap allows, so the flag says so and only the
    /// cap's worth are returned. The handler asks the repository for <c>MaxTrackedFlights + 1</c>
    /// deliberately — see the companion test below for why one extra row is what makes the flag
    /// exact. That the repository's own <c>LIMIT</c> is what bounds the count for real is proved
    /// against PostgreSQL in <c>AdminDrilldownRepositoryTests</c> (Infrastructure.Tests).
    /// </summary>
    [Fact]
    public async Task Handle_MoreTrackedFlightsThanTheCap_SetsTrackedFlightsTruncatedTrue()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register(travelerId, "heavy-flyer@example.com");

        var overCap = TrackedFlights(travelerId, GetAdminTravelerDetailQueryHandler.MaxTrackedFlights + 1);

        _travelers.GetDetailAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);
        _flights.GetRecentTravelerFlightsAsync(travelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<(TravelerFlight, Flight)>)overCap);
        _loyalty.GetAllMembershipsForTravelerAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TravelerLoyaltyMembership>)[]);

        var result = await _handler.Handle(new GetAdminTravelerDetailQuery(travelerId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(GetAdminTravelerDetailQueryHandler.MaxTrackedFlights, result!.TrackedFlights.Count);
        Assert.True(result.TrackedFlightsTruncated);

        // One more than the cap is what got asked for — the probe row that makes the flag exact.
        await _flights.Received(1).GetRecentTravelerFlightsAsync(
            travelerId, GetAdminTravelerDetailQueryHandler.MaxTrackedFlights + 1, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The boundary the probe row exists for: a traveler with <em>precisely</em>
    /// <see cref="GetAdminTravelerDetailQueryHandler.MaxTrackedFlights"/> flights is complete, not
    /// truncated. Inferring truncation from <c>Count == Max</c> would flag this response and send an
    /// operator to the generic browser for rows that are not there. This test fails under that
    /// inference and passes under the fetch-one-extra rule, which is the whole point of it.
    /// </summary>
    [Fact]
    public async Task Handle_TrackedFlightsExactlyAtTheCap_LeavesTrackedFlightsTruncatedFalse()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register(travelerId, "exactly-at-cap@example.com");

        var exactlyAtCap = TrackedFlights(travelerId, GetAdminTravelerDetailQueryHandler.MaxTrackedFlights);

        _travelers.GetDetailAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);
        _flights.GetRecentTravelerFlightsAsync(travelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<(TravelerFlight, Flight)>)exactlyAtCap);
        _loyalty.GetAllMembershipsForTravelerAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TravelerLoyaltyMembership>)[]);

        var result = await _handler.Handle(new GetAdminTravelerDetailQuery(travelerId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(GetAdminTravelerDetailQueryHandler.MaxTrackedFlights, result!.TrackedFlights.Count);
        Assert.False(result.TrackedFlightsTruncated);
    }

    /// <summary>
    /// #322 fix round, M4: PostgreSQL gives no row-order guarantee without an explicit
    /// <c>ORDER BY</c>, and device tokens are loaded through the same <c>Include</c> as the
    /// <c>Traveler</c> row rather than a separate <c>IQueryable</c> the repository could sort — so
    /// <see cref="GetAdminTravelerDetailQueryHandler"/> sorts them itself before mapping. Seeded
    /// deliberately out of chronological order to prove the handler is what re-orders them, not
    /// that they merely happened to already be sorted.
    /// </summary>
    [Fact]
    public async Task Handle_DeviceTokens_AreOrderedByLastSeenUtc_RegardlessOfInputOrder()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register(travelerId, "multi-device@example.com");
        var oldest = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var middle = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var newest = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        // Added out of order: newest first, oldest last.
        traveler.AddDeviceToken("ios", "token-newest", newest);
        traveler.AddDeviceToken("android", "token-oldest", oldest);
        traveler.AddDeviceToken("web", "token-middle", middle);

        _travelers.GetDetailAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);
        _flights.GetRecentTravelerFlightsAsync(travelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<(TravelerFlight, Flight)>)[]);
        _loyalty.GetAllMembershipsForTravelerAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TravelerLoyaltyMembership>)[]);

        var result = await _handler.Handle(new GetAdminTravelerDetailQuery(travelerId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.DeviceTokens.Count);
        Assert.Equal(["android", "web", "ios"], result.DeviceTokens.Select(t => t.Platform));
        Assert.Equal([oldest, middle, newest], result.DeviceTokens.Select(t => t.LastSeenUtc));
    }
}
