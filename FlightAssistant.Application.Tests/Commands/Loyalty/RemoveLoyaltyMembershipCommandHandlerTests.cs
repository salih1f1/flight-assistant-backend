using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Loyalty;

public class RemoveLoyaltyMembershipCommandHandlerTests
{
    private readonly ILoyaltyRepository _loyalty = Substitute.For<ILoyaltyRepository>();
    private readonly RemoveLoyaltyMembershipCommandHandler _handler;

    public RemoveLoyaltyMembershipCommandHandlerTests()
    {
        _handler = new RemoveLoyaltyMembershipCommandHandler(_loyalty);
    }

    [Fact]
    public async Task RemovesMembership_AndSaves()
    {
        var travelerId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var membership = TravelerLoyaltyMembership.Create(travelerId, 1, "NUM");
        _loyalty.GetMembershipByIdAsync(membershipId, Arg.Any<CancellationToken>()).Returns(membership);

        var cmd = new RemoveLoyaltyMembershipCommand(travelerId, membershipId);
        await _handler.Handle(cmd, CancellationToken.None);

        await _loyalty.Received(1).RemoveMembershipAsync(membership, Arg.Any<CancellationToken>());
        await _loyalty.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotFound_IsIdempotent_NoSaves()
    {
        _loyalty.GetMembershipByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var cmd = new RemoveLoyaltyMembershipCommand(Guid.NewGuid(), Guid.NewGuid());
        await _handler.Handle(cmd, CancellationToken.None); // should not throw

        await _loyalty.DidNotReceive().RemoveMembershipAsync(Arg.Any<TravelerLoyaltyMembership>(), Arg.Any<CancellationToken>());
        await _loyalty.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WrongTraveler_Throws()
    {
        var membershipId = Guid.NewGuid();
        var membership = TravelerLoyaltyMembership.Create(Guid.NewGuid(), 1, "NUM");
        _loyalty.GetMembershipByIdAsync(membershipId, Arg.Any<CancellationToken>()).Returns(membership);

        var cmd = new RemoveLoyaltyMembershipCommand(Guid.NewGuid() /* different */, membershipId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(cmd, CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
    }
}
