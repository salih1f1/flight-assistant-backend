using FlightAssistant.Application.EventHandlers;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Events;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlightAssistant.Application.Tests.EventHandlers;

public class FlightStatusChangedNotificationHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly ITravelerRepository _travelers = Substitute.For<ITravelerRepository>();
    private readonly IPushNotificationService _push = Substitute.For<IPushNotificationService>();
    private readonly FlightStatusChangedNotificationHandler _handler;

    private static readonly FlightStatusChanged TestEvent = new(
        FlightId: 1, FlightNumber: "BA123",
        ServiceDateUtc: DateOnly.FromDateTime(DateTime.UtcNow),
        StatusCode: "DLY", OccurredUtc: DateTime.UtcNow);

    public FlightStatusChangedNotificationHandlerTests()
    {
        _handler = new FlightStatusChangedNotificationHandler(
            _flights, _travelers, _push, NullLogger<FlightStatusChangedNotificationHandler>.Instance);
    }

    [Fact]
    public async Task Handle_TrackersWithTokens_SendsPushToAllTokens()
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);

        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");
        traveler.AddDeviceToken("ios", "token-bbb-222");

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });

        await _handler.Handle(TestEvent, default);

        await _push.Received(1).SendAsync(
            Arg.Is<IEnumerable<string>>(tokens => tokens.Count() == 2),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoTrackersWithNotifyEnabled_DoesNotSendPush()
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(false);

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });

        await _handler.Handle(TestEvent, default);

        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoTrackersAtAll_DoesNotSendPush()
    {
        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight>());

        await _handler.Handle(TestEvent, default);

        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TrackerWithNoDeviceTokens_DoesNotSendPush()
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);

        var traveler = Traveler.Register(travelerId, "test@example.com"); // no device tokens

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });

        await _handler.Handle(TestEvent, default);

        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PushServiceThrows_DoesNotPropagate()
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);

        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });
        _push.SendAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("APNs connection error"));

        // Should not throw — push failures must not crash the domain event handler
        var ex = await Record.ExceptionAsync(() => _handler.Handle(TestEvent, default));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Handle_MultipleTrackings_BatchLoadsTravelersOnce()
    {
        var travelerId1 = Guid.NewGuid();
        var travelerId2 = Guid.NewGuid();

        var tracking1 = TravelerFlight.Create(travelerId1, 1);
        tracking1.SetNotify(true);
        var tracking2 = TravelerFlight.Create(travelerId2, 1);
        tracking2.SetNotify(true);
        // Duplicate traveler id across two trackings to prove Distinct() dedup
        var tracking3 = TravelerFlight.Create(travelerId1, 1);
        tracking3.SetNotify(true);

        var traveler1 = Traveler.Register(travelerId1, "test1@example.com");
        traveler1.AddDeviceToken("ios", "token-aaa-111");
        var traveler2 = Traveler.Register(travelerId2, "test2@example.com");
        traveler2.AddDeviceToken("ios", "token-bbb-222");

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking1, tracking2, tracking3 });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler1, traveler2 });

        await _handler.Handle(TestEvent, default);

        await _push.Received(1).SendAsync(
            Arg.Is<IEnumerable<string>>(tokens => tokens.Count() == 3),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _travelers.Received(1).GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
        await _travelers.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PushReportsInvalidToken_RemovesItAndSavesOnce()
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);

        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "token-valid");
        traveler.AddDeviceToken("ios", "token-invalid");

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });
        _push.SendAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "token-invalid" });

        await _handler.Handle(TestEvent, default);

        Assert.DoesNotContain(traveler.DeviceTokens, t => t.Token == "token-invalid");
        Assert.Contains(traveler.DeviceTokens, t => t.Token == "token-valid");
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PushReportsNoInvalidTokens_DoesNotSave()
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);

        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "token-valid");

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });
        // NSubstitute auto-returns an empty collection for the unconfigured Task result.

        await _handler.Handle(TestEvent, default);

        await _travelers.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("SCH")]
    [InlineData("UNK")]
    [InlineData("XYZ")]
    public async Task Handle_NonActionableStatusCodes_DoesNotSendPush(string statusCode)
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);

        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");

        var evt = TestEvent with { StatusCode = statusCode };

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });

        await _handler.Handle(evt, default);

        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("DEP", "Your flight has departed.")]
    [InlineData("ARR", "Your flight has landed.")]
    [InlineData("DLY", "Your flight is delayed.")]
    [InlineData("CAN", "Your flight has been cancelled.")]
    [InlineData("DIV", "Your flight has been diverted.")]
    public async Task Handle_StatusCodes_ProduceCorrectMessage(string statusCode, string expectedBody)
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);

        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");

        var evt = TestEvent with { StatusCode = statusCode };

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });

        await _handler.Handle(evt, default);

        await _push.Received(1).SendAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            expectedBody,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AnEventThatShouldNotNotify_SendsNothing_AndLooksNothingUp()
    {
        await _handler.Handle(TestEvent with { StatusCode = "DEP", ShouldNotify = false }, default);

        await _flights.DidNotReceive().GetTrackingsByFlightIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ADelayWithMinutes_SaysHowLate()
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);
        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");
        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });

        await _handler.Handle(TestEvent with { StatusCode = "DLY", DelayMinutes = 40 }, default);

        await _push.Received(1).SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), "Your flight is delayed by 40 min.", Arg.Any<CancellationToken>());
    }
}
