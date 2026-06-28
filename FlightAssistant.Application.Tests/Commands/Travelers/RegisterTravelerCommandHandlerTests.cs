using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Travelers;

public class RegisterTravelerCommandHandlerTests
{
    private readonly ITravelerRepository _travelers = Substitute.For<ITravelerRepository>();
    private readonly RegisterTravelerCommandHandler _handler;

    public RegisterTravelerCommandHandlerTests()
    {
        _handler = new RegisterTravelerCommandHandler(_travelers);
    }

    [Fact]
    public async Task NewTraveler_RegistersAndReturnsNewId()
    {
        var oid = Guid.NewGuid();
        _travelers.GetByIdAsync(oid, Arg.Any<CancellationToken>()).ReturnsNull();

        Traveler? added = null;
        await _travelers.AddAsync(Arg.Do<Traveler>(t => added = t), Arg.Any<CancellationToken>());

        var cmd = new RegisterTravelerCommand(oid, "alice@example.com", "Alice", null);
        var id = await _handler.Handle(cmd, CancellationToken.None);

        Assert.Equal(oid, id);
        await _travelers.Received(1).AddAsync(Arg.Any<Traveler>(), Arg.Any<CancellationToken>());
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingTraveler_UnchangedIdentity_ReturnsExistingIdWithoutSaving()
    {
        var oid = Guid.NewGuid();
        var existing = Traveler.Register(oid, "alice@example.com", "Alice", "US");
        _travelers.GetByIdAsync(oid, Arg.Any<CancellationToken>()).Returns(existing);

        var cmd = new RegisterTravelerCommand(oid, "alice@example.com", "Alice", "US");
        var id = await _handler.Handle(cmd, CancellationToken.None);

        Assert.Equal(oid, id);
        await _travelers.DidNotReceive().AddAsync(Arg.Any<Traveler>(), Arg.Any<CancellationToken>());
        await _travelers.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingTraveler_ChangedIdentity_SyncsAndSaves()
    {
        var oid = Guid.NewGuid();
        var existing = Traveler.Register(oid, "old@example.com", "Old Name", "US");
        _travelers.GetByIdAsync(oid, Arg.Any<CancellationToken>()).Returns(existing);

        var cmd = new RegisterTravelerCommand(oid, "new@example.com", "New Name", "GB");
        var id = await _handler.Handle(cmd, CancellationToken.None);

        Assert.Equal(oid, id);
        Assert.Equal("new@example.com", existing.Email);
        Assert.Equal("New Name", existing.DisplayName);
        Assert.Equal("GB", existing.CountryCode);
        await _travelers.DidNotReceive().AddAsync(Arg.Any<Traveler>(), Arg.Any<CancellationToken>());
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
