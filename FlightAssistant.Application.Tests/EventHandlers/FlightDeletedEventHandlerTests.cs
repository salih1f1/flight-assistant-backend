using FlightAssistant.Application.EventHandlers;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Events;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightAssistant.Application.Tests.EventHandlers;

public class FlightDeletedEventHandlerTests
{
    private readonly ITravelerRepository _travelers = Substitute.For<ITravelerRepository>();
    private readonly IPushNotificationService _push = Substitute.For<IPushNotificationService>();
    private readonly FlightDeletedEventHandler _handler;

    public FlightDeletedEventHandlerTests()
    {
        _handler = new FlightDeletedEventHandler(
            _travelers, _push, NullLogger<FlightDeletedEventHandler>.Instance);
    }

    private static FlightDeleted Event(params Guid[] travelerIds) =>
        new(FlightId: 1, FlightNumber: "BA123",
            TrackerTravelerIds: travelerIds,
            OccurredUtc: DateTime.UtcNow);

    [Fact]
    public async Task Handle_TravelersWithTokens_SendsPushToAll()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var t1 = Traveler.Register("a@example.com");
        t1.AddDeviceToken("ios", "token-aaa");

        var t2 = Traveler.Register("b@example.com");
        t2.AddDeviceToken("ios", "token-bbb");
        t2.AddDeviceToken("ios", "token-ccc");

        _travelers.GetByIdAsync(id1, Arg.Any<CancellationToken>()).Returns(t1);
        _travelers.GetByIdAsync(id2, Arg.Any<CancellationToken>()).Returns(t2);

        await _handler.Handle(Event(id1, id2), default);

        await _push.Received(1).SendAsync(
            Arg.Is<IEnumerable<string>>(tokens => tokens.Count() == 3),
            Arg.Is<string>(s => s.Contains("BA123")),
            Arg.Is<string>(s => s.Contains("BA123")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TravelerNotFound_SkipsAndContinues()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var t2 = Traveler.Register("b@example.com");
        t2.AddDeviceToken("ios", "token-bbb");

        _travelers.GetByIdAsync(id1, Arg.Any<CancellationToken>()).Returns((Traveler?)null);
        _travelers.GetByIdAsync(id2, Arg.Any<CancellationToken>()).Returns(t2);

        await _handler.Handle(Event(id1, id2), default);

        await _push.Received(1).SendAsync(
            Arg.Is<IEnumerable<string>>(tokens => tokens.Count() == 1),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoDeviceTokens_DoesNotSendPush()
    {
        var id = Guid.NewGuid();
        var traveler = Traveler.Register("a@example.com"); // no tokens

        _travelers.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(traveler);

        await _handler.Handle(Event(id), default);

        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyTrackerList_DoesNotSendPush()
    {
        await _handler.Handle(Event(), default);

        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
