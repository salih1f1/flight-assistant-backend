using FlightAssistant.Application.Commands.Admin.Loyalty;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin;

public class AdminLoyaltyCommandHandlerTests
{
    private readonly ILoyaltyRepository _repo = Substitute.For<ILoyaltyRepository>();

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_CallsAddProgramAndSave_ReturnsId()
    {
        var handler = new CreateLoyaltyProgramCommandHandler(_repo);

        await handler.Handle(
            new CreateLoyaltyProgramCommand("BAEC", "BA Executive Club", 1, null, null),
            CancellationToken.None);

        await _repo.Received(1).AddProgramAsync(Arg.Any<LoyaltyProgram>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ExistingProgram_CallsSave()
    {
        var handler = new UpdateLoyaltyProgramCommandHandler(_repo);
        var program = LoyaltyProgram.Create("BAEC", "BA Executive Club", 1, null, null);
        _repo.GetProgramByIdAsync(1, Arg.Any<CancellationToken>()).Returns(program);

        await handler.Handle(
            new UpdateLoyaltyProgramCommand(1, "BAEC", "BA Executive Club Updated", 1, null, "OW"),
            CancellationToken.None);

        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        var handler = new UpdateLoyaltyProgramCommandHandler(_repo);
        _repo.GetProgramByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(
                new UpdateLoyaltyProgramCommand(99, "BAEC", "BA Executive Club", 1, null, null),
                CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);

        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingProgram_DeletesAndSaves()
    {
        var handler = new DeleteLoyaltyProgramCommandHandler(_repo);
        var program = LoyaltyProgram.Create("BAEC", "BA Executive Club", 1, null, null);
        _repo.GetProgramByIdAsync(1, Arg.Any<CancellationToken>()).Returns(program);

        await handler.Handle(new DeleteLoyaltyProgramCommand(1), CancellationToken.None);

        await _repo.Received(1).DeleteProgramAsync(program, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsWithoutSaving()
    {
        var handler = new DeleteLoyaltyProgramCommandHandler(_repo);
        _repo.GetProgramByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new DeleteLoyaltyProgramCommand(99), CancellationToken.None);

        await _repo.DidNotReceive().DeleteProgramAsync(Arg.Any<LoyaltyProgram>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
