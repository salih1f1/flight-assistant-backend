using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class SyncFlightStatusCommandHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IFlightStatusProvider _provider = Substitute.For<IFlightStatusProvider>();
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
        _handler = new SyncFlightStatusCommandHandler(_flights, _provider);
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

        var result = await _handler.Handle(new SyncFlightStatusCommand(null, 1), CancellationToken.None);

        Assert.True(result);
        await _flights.Received(1).AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        // Notification is now triggered via FlightStatusChanged domain event (dispatched by EF interceptor)
        // — no ISender call expected here
    }

    [Fact]
    public async Task DedupMatch_ReturnsFalseWithoutSaving()
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(TestFlight);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestStatus);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(new SyncFlightStatusCommand(null, 1), CancellationToken.None);

        Assert.False(result);
        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProviderReturnsNull_ReturnsFalse()
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(TestFlight);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        var result = await _handler.Handle(new SyncFlightStatusCommand(null, 1), CancellationToken.None);

        Assert.False(result);
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlightNotFound_Throws()
    {
        _flights.GetFlightByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new SyncFlightStatusCommand(null, 99), CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task UnknownStatusCode_Throws()
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(TestFlight);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestStatus with { StatusCode = "UNKNOWN" });
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _flights.GetFlightStatusIdByCodeAsync("UNKNOWN", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new SyncFlightStatusCommand(null, 1), CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProviderThrows_ExceptionPropagates()
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(TestFlight);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<FlightStatusResult?>(_ => throw new HttpRequestException("provider unavailable"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            _handler.Handle(new SyncFlightStatusCommand(null, 1), CancellationToken.None));

        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoDistinctStatuses_BothStored()
    {
        // First call: not a dedup
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(TestFlight);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestStatus);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _flights.GetFlightStatusIdByCodeAsync("DEP", Arg.Any<CancellationToken>()).Returns(2);

        var first = await _handler.Handle(new SyncFlightStatusCommand(null, 1), CancellationToken.None);

        // Second call: different effective time → different hash → also not a dedup
        var laterStatus = TestStatus with { EffectiveAtUtc = TestStatus.EffectiveAtUtc.AddMinutes(30) };
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(laterStatus);

        var second = await _handler.Handle(new SyncFlightStatusCommand(null, 1), CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        await _flights.Received(2).AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithTravelerId_NotTracking_ThrowsDomainException403()
    {
        var travelerId = Guid.NewGuid();
        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>())
            .ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new SyncFlightStatusCommand(travelerId, 1), CancellationToken.None));

        Assert.Equal(403, ex.StatusCode);
        await _flights.DidNotReceive().GetFlightByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
