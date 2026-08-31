using FlightAssistant.Application.Commands.Admin.Airports;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin;

public class AdminAirportCommandHandlerTests
{
    private readonly IAirportRepository _repo = Substitute.For<IAirportRepository>();

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_CallsAddAndSave_ReturnsId()
    {
        var handler = new CreateAirportCommandHandler(_repo);

        await handler.Handle(
            new CreateAirportCommand("LHR", null, "Heathrow", "London", "GB", 51.47, -0.46, 83),
            CancellationToken.None);

        await _repo.Received(1).AddAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ExistingAirport_CallsSave()
    {
        var handler = new UpdateAirportCommandHandler(_repo);
        var airport = Airport.Create("LHR", null, "Heathrow", "London", "GB", null, null, null);
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(airport);

        await handler.Handle(
            new UpdateAirportCommand(1, "LHR", null, "London Heathrow", "London", "GB", null, null, null),
            CancellationToken.None);

        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        var handler = new UpdateAirportCommandHandler(_repo);
        _repo.GetByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(
                new UpdateAirportCommand(99, "LHR", null, "Heathrow", "London", "GB", null, null, null),
                CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);

        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingAirport_DeletesAndSaves()
    {
        var handler = new DeleteAirportCommandHandler(_repo);
        var airport = Airport.Create("LHR", null, "Heathrow", "London", "GB", null, null, null);
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(airport);

        await handler.Handle(new DeleteAirportCommand(1), CancellationToken.None);

        await _repo.Received(1).DeleteAsync(airport, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsWithoutSaving()
    {
        var handler = new DeleteAirportCommandHandler(_repo);
        _repo.GetByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new DeleteAirportCommand(99), CancellationToken.None);

        await _repo.DidNotReceive().DeleteAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
