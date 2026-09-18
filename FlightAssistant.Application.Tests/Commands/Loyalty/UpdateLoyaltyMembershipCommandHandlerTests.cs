using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Loyalty;

public class UpdateLoyaltyMembershipCommandHandlerTests
{
    private readonly ILoyaltyRepository _loyalty = Substitute.For<ILoyaltyRepository>();
    private readonly UpdateLoyaltyMembershipCommandHandler _handler;

    public UpdateLoyaltyMembershipCommandHandlerTests()
    {
        _handler = new UpdateLoyaltyMembershipCommandHandler(_loyalty);
    }

    [Fact]
    public async Task UpdatesMembership_AndSaves()
    {
        var travelerId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var membership = TravelerLoyaltyMembership.Create(travelerId, 1, "OLD-NUM");

        _loyalty.GetMembershipByIdAsync(membershipId, Arg.Any<CancellationToken>()).Returns(membership);

        var cmd = new UpdateLoyaltyMembershipCommand(travelerId, membershipId, "NEW-NUM", 2, null);
        await _handler.Handle(cmd, CancellationToken.None);

        Assert.Equal("NEW-NUM", membership.AccountNumber);
        Assert.Equal(2, membership.TierId);
        await _loyalty.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WrongTraveler_Throws()
    {
        var membershipId = Guid.NewGuid();
        var membership = TravelerLoyaltyMembership.Create(Guid.NewGuid(), 1, "NUM");
        _loyalty.GetMembershipByIdAsync(membershipId, Arg.Any<CancellationToken>()).Returns(membership);

        var cmd = new UpdateLoyaltyMembershipCommand(Guid.NewGuid() /* different traveler */, membershipId, "NUM", null, null);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(cmd, CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task MembershipNotFound_Throws()
    {
        _loyalty.GetMembershipByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new UpdateLoyaltyMembershipCommand(Guid.NewGuid(), Guid.NewGuid(), "X", null, null), CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }
}
