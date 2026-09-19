using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.ReferenceData;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using FlightAssistant.Domain.Flights.Events;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class TrackFlightCommandHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IRefDataRepository _refData = Substitute.For<IRefDataRepository>();
    private readonly IReferenceDataProvisioner _provisioner = Substitute.For<IReferenceDataProvisioner>();
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
        public int MaxActiveTrackedFlightsPerTraveler => 3;
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
        _handler = new TrackFlightCommandHandler(_flights, _refData, _provisioner, new SyncOptions(), new WindowOptions(), _clock);

        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(1);
        _refData.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _refData.GetAirportIdByIataAsync("JFK", Arg.Any<CancellationToken>()).Returns(20);

        // Default: nothing can be provisioned, so every pre-#465 test continues to see its 404.
        _provisioner.ProvisionAsync(Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var first = ci.Arg<IReadOnlyCollection<MissingReference>>().First();
                return ReferenceProvisionResult.NotFound(first.Type, first.Code);
            });
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
    }

    // ── FlightTracked (#410, R1/R2): raised on every track request, ids only ──

    [Fact]
    public async Task HappyPath_RaisesFlightTracked_OnTheFlightTheCommandReturns_WithTheRightIds()
    {
        Flight? flight = null;
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(ci => flight = ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        var flightId = await _handler.Handle(DefaultCmd, CancellationToken.None);

        // Raised on flight (the same object GetOrCreateFlightAsync returned) whose Id is what the
        // command returned -- not some other aggregate the handler happened to touch.
        var evt = Assert.IsType<FlightTracked>(Assert.Single(flight!.DomainEvents));
        Assert.Equal(flightId, evt.FlightId);
        Assert.Equal(1, evt.AirlineId);             // "BA" stubbed to airline id 1
        Assert.Equal(10, evt.DepartureAirportId);   // "LHR" stubbed to 10
        Assert.Equal(20, evt.ArrivalAirportId);     // "JFK" stubbed to 20
    }

    [Fact]
    public async Task AlreadyTracking_StillRaisesFlightTracked_AndSavesUnconditionally()
    {
        // R1: a repeat track (the traveler already tracks this flight) still raises the event and
        // still saves, because the interceptor only dispatches events collected at SaveChanges time --
        // a save with nothing new to write is what makes the event go out at all here.
        var existingFlight = Flight.Create(1, "BA123", 10, 20, DefaultCmd.ServiceDate,
            DefaultCmd.ScheduledDepartureUtc, DefaultCmd.ScheduledArrivalUtc);
        var existingTracking = TravelerFlight.Create(DefaultCmd.TravelerId, existingFlight.Id);

        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(existingFlight);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existingFlight.Id, Arg.Any<CancellationToken>())
            .Returns(existingTracking);

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        var evt = Assert.IsType<FlightTracked>(Assert.Single(existingFlight.DomainEvents));
        Assert.Equal(existingFlight.Id, evt.FlightId);
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

    // ── Cap on tracked flights that haven't finished (#380); the fake's cap is 3 ──

    private static Flight ExistingFlight() => Flight.Create(
        1, "BA123", 10, 20, DefaultCmd.ServiceDate, DefaultCmd.ScheduledDepartureUtc, DefaultCmd.ScheduledArrivalUtc);

    [Fact]
    public async Task AtTheCap_ANewFlight_Throws409_AndWritesNothing()
    {
        _flights.CountActiveTrackedFlightsAsync(DefaultCmd.TravelerId, Now, TimeSpan.FromMinutes(20), TimeSpan.FromHours(12), Arg.Any<CancellationToken>()).Returns(3);
        _flights.GetByFlightNumberAndDateAsync(1, "BA123", DefaultCmd.ServiceDate, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("You can track up to 3 flights at a time. Stop tracking one to add another.", ex.Message);
        await _flights.DidNotReceive().GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneBelowTheCap_TracksTheFlight()
    {
        _flights.CountActiveTrackedFlightsAsync(DefaultCmd.TravelerId, Now, TimeSpan.FromMinutes(20), TimeSpan.FromHours(12), Arg.Any<CancellationToken>()).Returns(2);
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(ci => ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _flights.Received(1).AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AtTheCap_AFlightTheTravelerAlreadyTracks_StillReturnsItsId_WithoutAddingTracking()
    {
        var existing = ExistingFlight();
        _flights.CountActiveTrackedFlightsAsync(DefaultCmd.TravelerId, Now, TimeSpan.FromMinutes(20), TimeSpan.FromHours(12), Arg.Any<CancellationToken>()).Returns(3);
        _flights.GetByFlightNumberAndDateAsync(1, "BA123", DefaultCmd.ServiceDate, Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existing.Id, Arg.Any<CancellationToken>())
            .Returns(TravelerFlight.Create(DefaultCmd.TravelerId, existing.Id));

        var flightId = await _handler.Handle(DefaultCmd, CancellationToken.None);

        Assert.Equal(existing.Id, flightId);
        await _flights.DidNotReceive().AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AtTheCap_AnExistingFlightTheTravelerDoesNotTrack_Throws409()
    {
        var existing = ExistingFlight();
        _flights.CountActiveTrackedFlightsAsync(DefaultCmd.TravelerId, Now, TimeSpan.FromMinutes(20), TimeSpan.FromHours(12), Arg.Any<CancellationToken>()).Returns(3);
        _flights.GetByFlightNumberAndDateAsync(1, "BA123", DefaultCmd.ServiceDate, Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existing.Id, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        await _flights.DidNotReceive().AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AtTheCap_AnUnknownAirline_Is409_AndNeverReachesTheProvisioner()
    {
        // Superseded pre-#465 expectation: an unknown reference used to 404 regardless of the cap,
        // because the airline lookup threw before the cap check ever ran. Now that a missing
        // reference goes through the provisioner, the cap must be checked first (see "Why the cap
        // is checked first" in the task brief) -- a refused track must still write nothing,
        // including reference rows -- so this is a 409, not a 404.
        _flights.CountActiveTrackedFlightsAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(3);

        // "ZZ" has no stubbed airline, so the lookup finds nothing.
        var ex = await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(DefaultCmd with { FlightNumber = "ZZ123" }, CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        await _provisioner.DidNotReceive().ProvisionAsync(
            Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>());
    }

    // ── Reference-data provisioning (#465): a missing airline or airport is provisioned instead
    // of 404ing outright; only a source's genuine NotFound answer stays a 404. ──

    [Fact]
    public async Task EverythingKnown_NeverCallsTheProvisioner()
    {
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _provisioner.DidNotReceive().ProvisionAsync(
            Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_ThatProvisions_Succeeds()
    {
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _provisioner.ProvisionAsync(Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>())
            .Returns(ReferenceProvisionResult.Provisioned)
            .AndDoes(_ => _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(1));
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _provisioner.Received(1).ProvisionAsync(
            Arg.Is<IReadOnlyCollection<MissingReference>>(m =>
                m.Count == 1 &&
                m.Single().Type == VerificationEntityType.Airline &&
                m.Single().Code == "BA"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryMissingReference_IsSentInOneCall_AirlineFirstThenDepartureThenArrival()
    {
        // The order is load-bearing, not incidental: ReferenceDataProvisioner attributes every
        // failure to missing.First(), and ReferenceNotFound's doc comment claims the pre-#465 404
        // messages are unchanged only because this airline-first order reproduces the old
        // first-null-wins lookup sequence. Reordering these three MissingReference.Add calls would
        // silently change a client-visible 404 message with no other test catching it.
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _refData.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        _refData.GetAirportIdByIataAsync("JFK", Arg.Any<CancellationToken>()).ReturnsNull();

        await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(DefaultCmd, CancellationToken.None));

        await _provisioner.Received(1).ProvisionAsync(
            Arg.Is<IReadOnlyCollection<MissingReference>>(m =>
                m.Count == 3 &&
                m.ElementAt(0) == new MissingReference(VerificationEntityType.Airline, "BA") &&
                m.ElementAt(1) == new MissingReference(VerificationEntityType.Airport, "LHR") &&
                m.ElementAt(2) == new MissingReference(VerificationEntityType.Airport, "JFK")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_ThatTheSourceDoesNotHave_Still404sWithTheSameMessage()
    {
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("Airline 'BA' not found.", ex.Message);
    }

    [Fact]
    public async Task WhenProvisioningIsUnavailable_The503CarriesTheCauseAsDiagnosticDetail()
    {
        _refData.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        _provisioner.ProvisionAsync(Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>())
            .Returns(ReferenceProvisionResult.Unavailable(
                VerificationEntityType.Airport, "LHR", "ourairports download failed: HTTP 503"));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
        // Exact-equality, not DoesNotContain: missing.First() attribution can name the wrong entity
        // (see ReferenceDataProvisioner), so the client-facing message deliberately names none --
        // this pins that a future "helpful" edit can't interpolate FailedCode into it unnoticed.
        Assert.Equal(
            "Flight reference data could not be looked up right now. Please try again shortly.",
            ex.Message);
        Assert.Equal("ourairports download failed: HTTP 503", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task AtTheCap_WithAMissingReference_Is409AndProvisionsNothing()
    {
        // A refused track must write nothing at all — including reference rows. The cap here must
        // match SyncOptions.MaxActiveTrackedFlightsPerTraveler (3) — a value the pre-check reads
        // through _syncOptions, so an unrelated literal would prove nothing about the actual cap.
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _flights.CountActiveTrackedFlightsAsync(
                DefaultCmd.TravelerId, Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(3);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        await _provisioner.DidNotReceive().ProvisionAsync(
            Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>());
    }
}
