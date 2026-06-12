using FlightAssistant.Application.Commands.Admin.Lounges;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Lounges.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin;

public class AdminLoungeCommandHandlerTests
{
    private readonly ILoungeRepository _repo = Substitute.For<ILoungeRepository>();

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_CallsAddAndSave_ReturnsId()
    {
        var handler = new CreateLoungeCommandHandler(_repo);

        await handler.Handle(
            new CreateLoungeCommand(1, "Concorde Room", "T5", null, "24h", true, false, false, null),
            CancellationToken.None);

        await _repo.Received(1).AddAsync(Arg.Any<Lounge>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ExistingLounge_CallsSave()
    {
        var handler = new UpdateLoungeCommandHandler(_repo);
        var lounge = Lounge.Create(1, "Concorde Room", "T5", null, null, true, false, false, null);
        _repo.GetByIdWithProvidersAsync(1, Arg.Any<CancellationToken>()).Returns(lounge);

        await handler.Handle(
            new UpdateLoungeCommand(1, "Concorde Room Updated", "T5", null, "24h", true, false, false, "WiFi, Spa"),
            CancellationToken.None);

        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        var handler = new UpdateLoungeCommandHandler(_repo);
        _repo.GetByIdWithProvidersAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(
                new UpdateLoungeCommand(99, "Name", null, null, null, false, false, false, null),
                CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);

        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingLounge_DeletesAndSaves()
    {
        var handler = new DeleteLoungeCommandHandler(_repo);
        var lounge = Lounge.Create(1, "Concorde Room", null, null, null, true, false, false, null);
        _repo.GetByIdWithProvidersAsync(1, Arg.Any<CancellationToken>()).Returns(lounge);

        await handler.Handle(new DeleteLoungeCommand(1), CancellationToken.None);

        await _repo.Received(1).DeleteAsync(lounge, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsWithoutSaving()
    {
        var handler = new DeleteLoungeCommandHandler(_repo);
        _repo.GetByIdWithProvidersAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new DeleteLoungeCommand(99), CancellationToken.None);

        await _repo.DidNotReceive().DeleteAsync(Arg.Any<Lounge>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
