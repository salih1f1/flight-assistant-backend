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

        var traveler = Traveler.Register("test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");
        traveler.AddDeviceToken("ios", "token-bbb-222");

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns(traveler);

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

        var traveler = Traveler.Register("test@example.com"); // no device tokens

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns(traveler);

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

        var traveler = Traveler.Register("test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns(traveler);
        _push.SendAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("APNs connection error"));

        // Should not throw — push failures must not crash the domain event handler
        var ex = await Record.ExceptionAsync(() => _handler.Handle(TestEvent, default));
        Assert.Null(ex);
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

        var traveler = Traveler.Register("test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");

        var evt = TestEvent with { StatusCode = statusCode };

        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>())
            .Returns(traveler);

        await _handler.Handle(evt, default);

        await _push.Received(1).SendAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            expectedBody,
            Arg.Any<CancellationToken>());
    }
}
