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
        HasProgram(1, tierIds: [2]);

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

    /// <summary>A tier-only change: omitting the account number keeps the one already held (#548).</summary>
    [Fact]
    public async Task NullAccountNumber_KeepsCurrentAccountNumber()
    {
        var travelerId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var membership = TravelerLoyaltyMembership.Create(travelerId, 1, "KEEP-ME", tierId: 7);
        _loyalty.GetMembershipByIdAsync(membershipId, Arg.Any<CancellationToken>()).Returns(membership);
        HasProgram(1, tierIds: [7, 8]);

        await _handler.Handle(new UpdateLoyaltyMembershipCommand(travelerId, membershipId, null, 8, null), CancellationToken.None);

        Assert.Equal("KEEP-ME", membership.AccountNumber);
        Assert.Equal(8, membership.TierId);
        await _loyalty.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The update path carried no tier check at all while Add did (#475), so a tier of another
    /// program was saved as-is — a membership nobody holds (#548).
    /// </summary>
    [Fact]
    public async Task TierFromAnotherProgram_Throws409_AndWritesNothing()
    {
        var travelerId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var membership = TravelerLoyaltyMembership.Create(travelerId, 1, "NUM", tierId: 7);
        _loyalty.GetMembershipByIdAsync(membershipId, Arg.Any<CancellationToken>()).Returns(membership);
        HasProgram(1, tierIds: [7]);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new UpdateLoyaltyMembershipCommand(travelerId, membershipId, null, 99, null), CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("TierId 99", error.Message);
        Assert.Equal(7, membership.TierId);
        await _loyalty.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>Same helper as <c>AddLoyaltyMembershipCommandHandlerTests</c>: ids are database-assigned
    /// (private setters), so they go on by reflection.</summary>
    private void HasProgram(int programId, params int[] tierIds)
    {
        var program = LoyaltyProgram.Create($"P{programId}", "Test program", kindId: 1);
        SetId(program, programId);
        foreach (var tierId in tierIds)
        {
            var tier = LoyaltyTier.Create(programId, $"T{tierId}", $"Tier {tierId}", tierId);
            SetId(tier, tierId);
            program.AddTier(tier);
        }

        _loyalty.GetProgramByIdAsync(programId, Arg.Any<CancellationToken>()).Returns(program);
    }

    private static void SetId<TEntity>(TEntity entity, int id) =>
        typeof(TEntity).GetProperty("Id")!.SetValue(entity, id);
}
