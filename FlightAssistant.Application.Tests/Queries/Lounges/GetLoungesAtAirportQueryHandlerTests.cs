using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Lounges;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Lounges;

public class GetLoungesAtAirportQueryHandlerTests
{
    private readonly ILoungeRepository _lounges = Substitute.For<ILoungeRepository>();
    private readonly ILoyaltyRepository _loyalty = Substitute.For<ILoyaltyRepository>();
    private readonly GetLoungesAtAirportQueryHandler _handler;

    public GetLoungesAtAirportQueryHandlerTests()
    {
        _handler = new GetLoungesAtAirportQueryHandler(_lounges, _loyalty);
    }

    [Fact]
    public async Task Handle_NullTravelerId_ReturnsLoungesWithoutEligibility()
    {
        var lounge = Lounge.Create(1, "Test Lounge");
        _lounges.GetByAirportAsync("LHR", Arg.Any<CancellationToken>())
            .Returns(new List<Lounge> { lounge });

        var result = await _handler.Handle(new GetLoungesAtAirportQuery("LHR", null), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Test Lounge", result[0].Name);
        Assert.False(result[0].IsEligible);
        Assert.Null(result[0].EligibleVia);

        await _loyalty.DidNotReceive().GetMembershipsForTravelerAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithTravelerId_ComputesEligibility()
    {
        var travelerId = Guid.NewGuid();

        var provider = LoungeProvider.Create("AMEX_PLAT", "Amex Platinum Lounge Access", LoungeProviderKind.Card);
        var link = LoungeProviderLink.Create(1, 1, LoungeProviderRole.Operator);
        typeof(LoungeProviderLink).GetProperty(nameof(LoungeProviderLink.Provider))!.SetValue(link, provider);

        var lounge = Lounge.Create(1, "Test Lounge");
        lounge.ReplaceAllProviders(new[] { link });

        var program = LoyaltyProgram.Create("AMEX_PLAT", "Amex Platinum", kindId: 1);
        var membership = TravelerLoyaltyMembership.Create(travelerId, programId: 0, accountNumber: "ACC1");

        _lounges.GetByAirportAsync("LHR", Arg.Any<CancellationToken>())
            .Returns(new List<Lounge> { lounge });
        _loyalty.GetMembershipsForTravelerAsync(travelerId, 1, int.MaxValue, Arg.Any<CancellationToken>())
            .Returns((new List<TravelerLoyaltyMembership> { membership }, 1));
        _loyalty.GetProgramsAsync(1, int.MaxValue, Arg.Any<CancellationToken>())
            .Returns((new List<LoyaltyProgram> { program }, 1));

        var result = await _handler.Handle(new GetLoungesAtAirportQuery("LHR", travelerId), CancellationToken.None);

        Assert.Single(result);
        Assert.True(result[0].IsEligible);
        Assert.Equal("Amex Platinum Lounge Access", result[0].EligibleVia);

        await _loyalty.Received(1).GetMembershipsForTravelerAsync(travelerId, 1, int.MaxValue, Arg.Any<CancellationToken>());
    }
}
