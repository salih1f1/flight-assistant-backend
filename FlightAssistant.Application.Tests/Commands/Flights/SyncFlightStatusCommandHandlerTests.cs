using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.EventHandlers;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using MediatR;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class SyncFlightStatusCommandHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IFlightStatusProvider _provider = Substitute.For<IFlightStatusProvider>();
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly SyncFlightStatusCommandHandler _handler;

    private static readonly Flight TestFlight = Flight.Create(
        1, "BA123", 10, 20,
        DateOnly.FromDateTime(DateTime.Today),
        DateTime.UtcNow, DateTime.UtcNow.AddHours(8));

    private static readonly FlightStatusResult TestStatus = new(
        StatusCode: "DEP",
        EffectiveAtUtc: DateTime.UtcNow,
        EstimatedDepartureUtc: DateTime.UtcNow,
        EstimatedArrivalUtc: DateTime.UtcNow.AddHours(8),
        ActualDepartureUtc: DateTime.UtcNow,
        ActualArrivalUtc: null,
        Gate: "A1",
        Terminal: "T5",
        BaggageClaim: null,
        EquipmentIcao: "B789",
        ExternalId: "ext-1",
        RawPayloadJson: null);

    public SyncFlightStatusCommandHandlerTests()
    {
        _handler = new SyncFlightStatusCommandHandler(_flights, _provider, _sender);
        _provider.ProviderName.Returns("TestProvider");
    }

    [Fact]
    public async Task StatusChanged_RecordsEventAndReturnsTrue()
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(TestFlight);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestStatus);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _flights.GetFlightStatusIdByCodeAsync("DEP", Arg.Any<CancellationToken>()).Returns(2);

        var result = await _handler.Handle(new SyncFlightStatusCommand(1), CancellationToken.None);

        Assert.True(result);
        await _flights.Received(1).AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _sender.Received(1).Send(
            Arg.Is<SendFlightStatusNotificationCommand>(c => c.FlightId == 1 && c.StatusCode == "DEP"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DedupMatch_ReturnsFalseWithoutSaving()
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(TestFlight);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestStatus);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(new SyncFlightStatusCommand(1), CancellationToken.None);

        Assert.False(result);
        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _sender.DidNotReceive().Send(Arg.Any<IRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProviderReturnsNull_ReturnsFalse()
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(TestFlight);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        var result = await _handler.Handle(new SyncFlightStatusCommand(1), CancellationToken.None);

        Assert.False(result);
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlightNotFound_Throws()
    {
        _flights.GetFlightByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _handler.Handle(new SyncFlightStatusCommand(99), CancellationToken.None));
    }
}
