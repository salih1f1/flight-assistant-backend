using FlightAssistant.Application.Commands.Admin.Travelers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Travelers;

public class DeleteDeviceTokenCommandHandlerTests
{
    private readonly IAdminTravelerRepository _travelers = Substitute.For<IAdminTravelerRepository>();
    private readonly DeleteDeviceTokenCommandHandler _handler;

    public DeleteDeviceTokenCommandHandlerTests()
        => _handler = new DeleteDeviceTokenCommandHandler(_travelers);

    [Fact]
    public async Task Handle_TokenExists_RemovesAndSaves()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register(travelerId, "test@example.com");
        var token = traveler.AddDeviceToken("ios", "token123");

        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);

        await _handler.Handle(new DeleteDeviceTokenCommand(travelerId, token.Id), CancellationToken.None);

        Assert.Empty(traveler.DeviceTokens);
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TravelerNotFound_ThrowsDomainException404_AndNeverSaves()
    {
        _travelers.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new DeleteDeviceTokenCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        await _travelers.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The case #322 calls out explicitly: an unknown token 404s with no audit row. Nothing here
    /// asserts the audit row directly (that needs the real interceptor against real PostgreSQL —
    /// see the Infrastructure-suite test), but "never saves" is the handler-level half of that
    /// guarantee: <c>AdminAuditInterceptor</c> can only write a row inside a
    /// <c>SaveChangesAsync</c> call that actually happens, so a handler that never calls it can
    /// never produce one, regardless of what the interceptor does.
    /// </summary>
    [Fact]
    public async Task Handle_TokenNotFoundOnTraveler_ThrowsDomainException404_AndNeverSaves()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "some-other-token");

        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);

        var unknownTokenId = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new DeleteDeviceTokenCommand(travelerId, unknownTokenId), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Single(traveler.DeviceTokens); // untouched
        await _travelers.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
