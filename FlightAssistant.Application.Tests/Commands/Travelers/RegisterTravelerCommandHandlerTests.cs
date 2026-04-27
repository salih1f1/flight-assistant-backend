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
        _travelers.GetByEmailAsync("alice@example.com", Arg.Any<CancellationToken>()).ReturnsNull();

        Traveler? added = null;
        await _travelers.AddAsync(Arg.Do<Traveler>(t => added = t), Arg.Any<CancellationToken>());

        var cmd = new RegisterTravelerCommand(Guid.NewGuid(), "alice@example.com", "Alice", null);
        var id = await _handler.Handle(cmd, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        await _travelers.Received(1).AddAsync(Arg.Any<Traveler>(), Arg.Any<CancellationToken>());
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingTraveler_ReturnsExistingIdWithoutSaving()
    {
        var existing = Traveler.Register("alice@example.com", "Alice");
        _travelers.GetByEmailAsync("alice@example.com", Arg.Any<CancellationToken>()).Returns(existing);

        var cmd = new RegisterTravelerCommand(Guid.NewGuid(), "alice@example.com", "Alice", null);
        var id = await _handler.Handle(cmd, CancellationToken.None);

        Assert.Equal(existing.Id, id);
        await _travelers.DidNotReceive().AddAsync(Arg.Any<Traveler>(), Arg.Any<CancellationToken>());
        await _travelers.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
