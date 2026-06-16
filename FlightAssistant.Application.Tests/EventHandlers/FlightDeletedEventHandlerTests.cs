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

        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { t1, t2 });

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

        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { t2 });

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

        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });

        await _handler.Handle(Event(id), default);

        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyTrackerList_DoesNotSendPush()
    {
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler>());

        await _handler.Handle(Event(), default);

        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PushReportsInvalidToken_RemovesItAndSavesOnce()
    {
        var id = Guid.NewGuid();
        var traveler = Traveler.Register("a@example.com");
        traveler.AddDeviceToken("ios", "token-valid");
        traveler.AddDeviceToken("ios", "token-invalid");

        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });
        _push.SendAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "token-invalid" });

        await _handler.Handle(Event(id), default);

        Assert.DoesNotContain(traveler.DeviceTokens, t => t.Token == "token-invalid");
        Assert.Contains(traveler.DeviceTokens, t => t.Token == "token-valid");
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PushReportsNoInvalidTokens_DoesNotSave()
    {
        var id = Guid.NewGuid();
        var traveler = Traveler.Register("a@example.com");
        traveler.AddDeviceToken("ios", "token-valid");

        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });

        await _handler.Handle(Event(id), default);

        await _travelers.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DuplicateTravelerIds_BatchLoadsTravelersOnceWithDistinctIds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var t1 = Traveler.Register("a@example.com");
        t1.AddDeviceToken("ios", "token-aaa");

        var t2 = Traveler.Register("b@example.com");
        t2.AddDeviceToken("ios", "token-bbb");
        t2.AddDeviceToken("ios", "token-ccc");

        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { t1, t2 });

        // Duplicate traveler id (id1) to prove Distinct() dedup
        await _handler.Handle(Event(id1, id1, id2), default);

        await _push.Received(1).SendAsync(
            Arg.Is<IEnumerable<string>>(tokens => tokens.Count() == 3),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _travelers.Received(1).GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
        await _travelers.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
