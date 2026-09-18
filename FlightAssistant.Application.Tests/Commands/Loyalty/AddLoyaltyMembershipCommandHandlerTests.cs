using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
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
        HasProgram(1);
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

    [Fact]
    public async Task AcceptsATierOfTheProgram()
    {
        HasProgram(1, tierIds: [7, 8]);
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "BA123456", 8, null, null);

        await _handler.Handle(cmd, CancellationToken.None);

        await _loyalty.Received(1).AddMembershipAsync(
            Arg.Is<TravelerLoyaltyMembership>(m => m.TierId == 8), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The program is a foreign key of <c>traveler_loyalty_memberships</c>, and the validator only
    /// checks that the id is positive — before #475 an unknown one reached Postgres and failed its
    /// constraint as a 500.
    /// </summary>
    [Fact]
    public async Task UnknownProgram_Throws409_AndWritesNothing()
    {
        _loyalty.GetProgramByIdAsync(1, Arg.Any<CancellationToken>()).Returns((LoyaltyProgram?)null);
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "BA123456", null, null, null);

        var error = await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(cmd, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("ProgramId 1", error.Message);
        await _loyalty.DidNotReceive().AddMembershipAsync(Arg.Any<TravelerLoyaltyMembership>(), Arg.Any<CancellationToken>());
        await _loyalty.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A tier that exists, but under another program: both foreign keys hold, and the pair still
    /// describes a membership nobody holds.
    /// </summary>
    [Fact]
    public async Task TierFromAnotherProgram_Throws409_AndWritesNothing()
    {
        HasProgram(1, tierIds: [7]);
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "BA123456", 99, null, null);

        var error = await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(cmd, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("TierId 99", error.Message);
        await _loyalty.DidNotReceive().AddMembershipAsync(Arg.Any<TravelerLoyaltyMembership>(), Arg.Any<CancellationToken>());
        await _loyalty.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>A program the repository knows, with the tiers it carries. Ids are database-assigned
    /// (private setters), so they go on by reflection — as elsewhere in this suite.</summary>
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
