using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Travelers;

public class AddDeviceTokenCommandHandlerTests
{
    private readonly ITravelerRepository _travelers = Substitute.For<ITravelerRepository>();
    private readonly AddDeviceTokenCommandHandler _handler;

    public AddDeviceTokenCommandHandlerTests()
    {
        _handler = new AddDeviceTokenCommandHandler(_travelers);
    }

    [Fact]
    public async Task AddsToken_AndSaves()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register("bob@example.com");
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);

        var cmd = new AddDeviceTokenCommand(travelerId, "ios", "device-token-abc");
        await _handler.Handle(cmd, CancellationToken.None);

        Assert.Single(traveler.DeviceTokens);
        Assert.Equal("device-token-abc", traveler.DeviceTokens.First().Token);
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DuplicateToken_IsIdempotent()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register("bob@example.com");
        traveler.AddDeviceToken("ios", "device-token-abc");
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);

        // Add same token again
        var cmd = new AddDeviceTokenCommand(travelerId, "ios", "device-token-abc");
        await _handler.Handle(cmd, CancellationToken.None);

        // Still only one token
        Assert.Single(traveler.DeviceTokens);
    }

    [Fact]
    public async Task TravelerNotFound_Throws()
    {
        _travelers.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _handler.Handle(new AddDeviceTokenCommand(Guid.NewGuid(), "ios", "tok"), CancellationToken.None));
    }
}
