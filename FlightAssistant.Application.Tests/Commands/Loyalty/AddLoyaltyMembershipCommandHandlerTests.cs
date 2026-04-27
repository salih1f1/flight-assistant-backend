using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Commands.Loyalty;

public class AddLoyaltyMembershipCommandHandlerTests
{
    private readonly ILoyaltyRepository _loyalty = Substitute.For<ILoyaltyRepository>();
    private readonly AddLoyaltyMembershipCommandHandler _handler;

    public AddLoyaltyMembershipCommandHandlerTests()
    {
        _handler = new AddLoyaltyMembershipCommandHandler(_loyalty);
    }

    [Fact]
    public async Task AddsMembership_ReturnsNewId()
    {
        var travelerId = Guid.NewGuid();
        var cmd = new AddLoyaltyMembershipCommand(travelerId, 1, "BA123456", null, null, null);

        var id = await _handler.Handle(cmd, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        await _loyalty.Received(1).AddMembershipAsync(
            Arg.Is<TravelerLoyaltyMembership>(m =>
                m.TravelerId == travelerId &&
                m.ProgramId == 1 &&
                m.AccountNumber == "BA123456"),
            Arg.Any<CancellationToken>());
        await _loyalty.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
