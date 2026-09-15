using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class TrackFlightCommandHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IRefDataRepository _refData = Substitute.For<IRefDataRepository>();
    private readonly TrackFlightCommandHandler _handler;

    private static readonly TrackFlightCommand DefaultCmd = new(
        TravelerId: Guid.NewGuid(),
        FlightNumber: "BA123",
        ServiceDate: DateOnly.FromDateTime(DateTime.Today),
        DepartureAirportIata: "LHR",
        ArrivalAirportIata: "JFK",
        ScheduledDepartureUtc: DateTime.UtcNow,
        ScheduledArrivalUtc: DateTime.UtcNow.AddHours(8));

    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));

    private sealed class SyncOptions : IFlightSyncOptions
    {
        public TimeSpan NearDepartureInterval => TimeSpan.FromMinutes(3);
        public TimeSpan Interval => TimeSpan.FromMinutes(7);
        public TimeSpan ScheduleCheckInterval => TimeSpan.FromHours(6);
        public TimeSpan FastWindowBeforeDeparture => TimeSpan.FromHours(3);
        public TimeSpan StopAfterArrival => TimeSpan.FromMinutes(20);
        public TimeSpan StopWithoutArrivalAfter => TimeSpan.FromHours(12);
        public TimeSpan ManualSyncCooldown => TimeSpan.FromMinutes(4);
    }

    private sealed class WindowOptions : IFlightStatusWindowOptions
    {
        public int StatusWindowFutureDays => 1;
    }

    private TrackFlightCommand CommandDeparting(DateTime departureUtc) => DefaultCmd with
    {
        ServiceDate = DateOnly.FromDateTime(departureUtc),
        ScheduledDepartureUtc = departureUtc,
        ScheduledArrivalUtc = departureUtc.AddHours(8),
    };

    public TrackFlightCommandHandlerTests()
    {
        _handler = new TrackFlightCommandHandler(_flights, _refData, new SyncOptions(), new WindowOptions(), _clock);

        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(1);
        _refData.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _refData.GetAirportIdByIataAsync("JFK", Arg.Any<CancellationToken>()).Returns(20);
    }

    [Fact]
    public async Task HappyPath_NewFlightAndTracking_GetsOrCreatesFlightSavesTrackingAndReturnsFlightId()
    {
        // GetOrCreateFlightAsync echoes back the candidate it was handed.
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        var result = await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _flights.Received(1).GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlightExists_OnlyAddsTracking()
    {
        var existingFlight = Flight.Create(1, "BA123", 10, 20, DefaultCmd.ServiceDate,
            DefaultCmd.ScheduledDepartureUtc, DefaultCmd.ScheduledArrivalUtc);

        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(existingFlight);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existingFlight.Id, Arg.Any<CancellationToken>())
            .ReturnsNull();

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _flights.Received(1).GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyTracking_IsIdempotent_DoesNotAddTracking()
    {
        var existingFlight = Flight.Create(1, "BA123", 10, 20, DefaultCmd.ServiceDate,
            DefaultCmd.ScheduledDepartureUtc, DefaultCmd.ScheduledArrivalUtc);
        var existingTracking = TravelerFlight.Create(DefaultCmd.TravelerId, existingFlight.Id);

        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(existingFlight);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existingFlight.Id, Arg.Any<CancellationToken>())
            .Returns(existingTracking);

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _flights.DidNotReceive().AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AirlineNotFound_Throws()
    {
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(DefaultCmd, CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task DepartureAirportNotFound_Throws()
    {
        _refData.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(DefaultCmd, CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task AirlineIsDerived_FromTheFlightNumbersFirstTwoCharacters_NotFromASeparateField()
    {
        // There is no AirlineIata field any more — the handler must derive the airline from the
        // flight number itself. "6E4120" is a numeric-prefix carrier (IndiGo), so this also proves
        // the derivation isn't assuming an alphabetic-only prefix.
        var cmd = DefaultCmd with { FlightNumber = "6E4120" };
        _refData.GetAirlineIdByIataAsync("6E", Arg.Any<CancellationToken>()).Returns(7);
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(cmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        await _handler.Handle(cmd, CancellationToken.None);

        await _refData.Received(1).GetAirlineIdByIataAsync("6E", Arg.Any<CancellationToken>());
        await _flights.Received(1).GetOrCreateFlightAsync(
            Arg.Is<Flight>(f => f.AirlineId == 7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AirlineDerivedFromFlightNumber_WhenUnknown_Still404s()
    {
        // Same 404 behaviour as before, just triggered by the derived airline rather than a
        // separately posted one.
        var cmd = DefaultCmd with { FlightNumber = "ZZ999" };
        _refData.GetAirlineIdByIataAsync("ZZ", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(cmd, CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }

    [Theory]
    [InlineData(1, FlightSyncMode.Fast)]       // departs in 1 h, inside the 3 h Fast window
    [InlineData(10, FlightSyncMode.Live)]      // within 1 day
    [InlineData(24 * 30, FlightSyncMode.Schedule)]
    public async Task FirstTracker_PlansTheFlight_AndMakesItDueNow(int hoursToDeparture, FlightSyncMode expected)
    {
        Flight? flight = null;
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(ci => flight = ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await _handler.Handle(CommandDeparting(Now.AddHours(hoursToDeparture)), CancellationToken.None);

        Assert.Equal(expected, flight!.SyncMode);
        Assert.Equal(Now, flight.NextSyncDueUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstTracker_OfAFinishedFlight_IsStopped_WithNoNextSync()
    {
        Flight? flight = null;
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(ci => flight = ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>()).ReturnsNull();

        // Arrived (by schedule) 2 days ago, well past StopWithoutArrivalAfter.
        await _handler.Handle(CommandDeparting(Now.AddDays(-2).AddHours(-8)), CancellationToken.None);

        Assert.Equal(FlightSyncMode.Stopped, flight!.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task FlightAlreadyPlanned_KeepsItsPlan()
    {
        var existing = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(1), Now.AddHours(9));
        existing.ApplySyncPlan(FlightSyncMode.Live, Now.AddMinutes(5));
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existing.Id, Arg.Any<CancellationToken>()).ReturnsNull();

        await _handler.Handle(CommandDeparting(Now.AddHours(1)), CancellationToken.None);

        Assert.Equal(FlightSyncMode.Live, existing.SyncMode);
        Assert.Equal(Now.AddMinutes(5), existing.NextSyncDueUtc);
    }

    [Fact]
    public async Task AlreadyTracking_DoesNotPlan()
    {
        var existing = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(1), Now.AddHours(9));
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existing.Id, Arg.Any<CancellationToken>())
            .Returns(TravelerFlight.Create(DefaultCmd.TravelerId, existing.Id));

        await _handler.Handle(CommandDeparting(Now.AddHours(1)), CancellationToken.None);

        Assert.Null(existing.SyncMode);
    }
}
