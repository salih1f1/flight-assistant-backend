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
        await _flights.DidNotReceive().GetAllTravelerFlightsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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
        _flights.GetAllTravelerFlightsAsync(travelerId, Arg.Any<CancellationToken>())
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
        _flights.GetAllTravelerFlightsAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<(TravelerFlight, Flight)>)[]);
        _loyalty.GetAllMembershipsForTravelerAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TravelerLoyaltyMembership>)[]);

        var result = await _handler.Handle(new GetAdminTravelerDetailQuery(travelerId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.DeviceTokens);
        Assert.Empty(result.TrackedFlights);
        Assert.Empty(result.LoyaltyMemberships);
    }
}
