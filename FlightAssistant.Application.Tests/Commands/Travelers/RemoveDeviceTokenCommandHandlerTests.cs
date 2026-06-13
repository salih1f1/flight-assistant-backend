using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Travelers;

public class RemoveDeviceTokenCommandHandlerTests
{
    private readonly ITravelerRepository _travelers = Substitute.For<ITravelerRepository>();
    private readonly RemoveDeviceTokenCommandHandler _handler;

    public RemoveDeviceTokenCommandHandlerTests()
    {
        _handler = new RemoveDeviceTokenCommandHandler(_travelers);
    }

    [Fact]
    public async Task Handle_TokenExists_RemovesAndSaves()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register(travelerId, "test@example.com");
        var token = traveler.AddDeviceToken("ios", "token123");

        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);

        await _handler.Handle(new RemoveDeviceTokenCommand(travelerId, token.Id), CancellationToken.None);

        Assert.Empty(traveler.DeviceTokens);
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TravelerNotFound_ThrowsDomainException()
    {
        _travelers.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new RemoveDeviceTokenCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
    }
}
