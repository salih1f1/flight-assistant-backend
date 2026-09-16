using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.FlightSync;

public class FlightStatusApplierTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    private const string Source = "TestProvider";

    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly FlightStatusApplier _applier;

    public FlightStatusApplierTests()
    {
        _applier = new FlightStatusApplier(_flights, NullLogger<FlightStatusApplier>.Instance);
        _flights.GetFlightStatusIdByCodeAsync("DEP", Arg.Any<CancellationToken>()).Returns(2);
        _flights.GetFlightStatusIdByCodeAsync("UNK", Arg.Any<CancellationToken>()).Returns(7);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
    }

    private static Flight AFlight() =>
        Flight.Create(1, "BA123", 10, 20, new DateOnly(2026, 9, 14), Now.AddHours(1), Now.AddHours(9));

    private static FlightStatusResult Status(string code = "DEP", string? gate = "A1") => new(
        StatusCode: code,
        EffectiveAtUtc: Now,
        EstimatedDepartureUtc: null,
        EstimatedArrivalUtc: null,
        ActualDepartureUtc: null,
        ActualArrivalUtc: null,
        Gate: gate,
        Terminal: "T5",
        BaggageClaim: null,
        EquipmentIcao: "B789",
        ExternalId: "ext-1",
        RawPayloadJson: null);

    private Task<bool> Apply(Flight flight, FlightStatusResult result) =>
        _applier.ApplyAsync(flight, result, Source, Now, CancellationToken.None);

    [Fact]
    public async Task ANewStatus_RecordsTheEvent_AndNeverSaves()
    {
        var flight = AFlight();
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>()).ReturnsNull();

        Assert.True(await Apply(flight, Status()));

        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e => e.StatusId == 2 && e.Source == Source && e.Gate == "A1"),
            Arg.Any<CancellationToken>());
        Assert.True(Assert.IsType<FlightStatusChanged>(Assert.Single(flight.DomainEvents)).ShouldNotify);
        // The caller owns the unit of work.
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnrecognisedCode_FallsBackToUnknown()
    {
        var flight = AFlight();
        _flights.GetFlightStatusIdByCodeAsync("WAT", Arg.Any<CancellationToken>()).ReturnsNull();
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>()).ReturnsNull();

        Assert.True(await Apply(flight, Status("WAT")));

        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e => e.StatusId == 7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnseededUnknownCode_Throws()
    {
        var flight = AFlight();
        _flights.GetFlightStatusIdByCodeAsync("WAT", Arg.Any<CancellationToken>()).ReturnsNull();
        _flights.GetFlightStatusIdByCodeAsync("UNK", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Apply(flight, Status("WAT")));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task AStatusTheFlightAlreadyHas_WithANewGate_RecordsADetailChange_WithNoPush()
    {
        var flight = AFlight();
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>())
            .Returns(FlightStatusEvent.Create(flight.Id, 2, Now, Source, "ext-1", "A1", "T5"));

        Assert.True(await Apply(flight, Status(gate: "B7")));

        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e => e.Gate == "B7"), Arg.Any<CancellationToken>());
        Assert.Empty(flight.DomainEvents);
    }

    [Fact]
    public async Task AStatusTheFlightAlreadyHas_WithNothingNew_RecordsNothing()
    {
        var flight = AFlight();
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>())
            .Returns(FlightStatusEvent.Create(flight.Id, 2, Now, Source, "ext-1", "A1", "T5"));

        Assert.False(await Apply(flight, Status()));

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        Assert.Empty(flight.DomainEvents);
    }

    [Fact]
    public async Task TheSameStatusAsTheLatestEvent_RecordsIt_WithoutAPush()
    {
        var flight = AFlight();
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>())
            .Returns(FlightStatusEvent.Create(flight.Id, 2, Now.AddMinutes(-5), Source, "ext-0"));

        Assert.True(await Apply(flight, Status()));

        Assert.False(Assert.IsType<FlightStatusChanged>(Assert.Single(flight.DomainEvents)).ShouldNotify);
    }

    [Fact]
    public async Task TheSourceReachesTheEventAndItsHash()
    {
        var flight = AFlight();
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>()).ReturnsNull();

        await _applier.ApplyAsync(flight, Status(), "AnotherProvider", Now, CancellationToken.None);

        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e =>
                e.Source == "AnotherProvider"
                && e.DedupHash == FlightStatusEvent.ComputeDedupHash(flight.Id, 2, Now, "AnotherProvider", "ext-1")),
            Arg.Any<CancellationToken>());
    }
}
