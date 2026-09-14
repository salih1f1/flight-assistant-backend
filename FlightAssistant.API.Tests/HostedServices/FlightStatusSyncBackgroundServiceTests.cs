using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.API.Tests.HostedServices;

public class FlightStatusSyncBackgroundServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly FlightSyncHeartbeat _heartbeat = new();
    private int _mediatorScopes;

    private FlightStatusSyncBackgroundService Service(FlightSyncOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _flights);
        // Counts how many scopes resolved a mediator: one per flight.
        services.AddScoped(_ => { _mediatorScopes++; return _mediator; });

        return new FlightStatusSyncBackgroundService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? new FlightSyncOptions()),
            _clock,
            NullLogger<FlightStatusSyncBackgroundService>.Instance,
            _heartbeat);
    }

    private void DueIds(params int[] ids) =>
        _flights.GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>()).Returns(ids.ToList());

    private List<SyncFlightStatusCommand> Sent() => _mediator.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<SyncFlightStatusCommand>().ToList();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 5 s.");
            await Task.Delay(10);
        }
    }

    // ── RunOnceAsync ──

    [Fact]
    public async Task RunOnce_SendsAScheduledSyncForEachDueFlight_InTheRepositorysOrder()
    {
        DueIds(3, 1, 2);

        var result = await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { 3, 1, 2 }, Sent().Select(c => c.FlightId));
        Assert.All(Sent(), c =>
        {
            Assert.Equal(SyncTrigger.Scheduled, c.Trigger);
            Assert.Null(c.TravelerId);
        });
        Assert.Equal(new FlightSyncRunResult(3, 0), result);
    }

    [Fact]
    public async Task RunOnce_NoDueFlights_SendsNothing()
    {
        DueIds();

        Assert.Equal(new FlightSyncRunResult(0, 0), await Service().RunOnceAsync(CancellationToken.None));
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task RunOnce_AFailingFlight_IsCounted_AndTheRestStillSync()
    {
        DueIds(1, 2, 3);
        _mediator.Send(Arg.Is<SyncFlightStatusCommand>(c => c.FlightId == 1), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));

        var result = await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(new FlightSyncRunResult(3, 1), result);
        Assert.Equal(new[] { 1, 2, 3 }, Sent().Select(c => c.FlightId));
    }

    [Fact]
    public async Task RunOnce_EachFlightSyncsInItsOwnScope()
    {
        // A shared DbContext would let one flight's failed, half-applied changes be saved by the next.
        DueIds(1, 2, 3);

        await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, _mediatorScopes);
    }

    // ── ExecuteAsync ──

    [Fact]
    public async Task Disabled_NeverLooksForFlights_AndTheHeartbeatStaysNeverRun()
    {
        var service = Service(new FlightSyncOptions { Enabled = false });

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        await _flights.DidNotReceive().GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());
        Assert.Equal(FlightSyncOutcome.NeverRun, _heartbeat.Current.LastOutcome);
    }

    [Fact]
    public async Task Enabled_RunsAtOnce_ThenOnEveryLoopInterval()
    {
        DueIds();
        var service = Service(new FlightSyncOptions { LoopInterval = TimeSpan.FromSeconds(30) }); // non-default

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => _heartbeat.Current.LastOutcome == FlightSyncOutcome.Success);
            Assert.Equal(Now.AddSeconds(30), _heartbeat.Current.NextRunDueUtc);
            await _flights.Received(1).GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());

            _clock.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(100);
            await _flights.Received(1).GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());

            _clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntil(() => _flights.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IFlightRepository.GetDueTrackedFlightIdsAsync)) == 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ── #344 end to end: real handler and planner, fake clock and repository ──

    private static Flight WithId(Flight flight, int id)
    {
        typeof(BaseIntEntity).GetProperty(nameof(BaseIntEntity.Id))!.SetValue(flight, id);
        return flight;
    }

    [Fact]
    public async Task OverTenMinutes_AFlightNearDeparture_DoesNotSpeedUpTheOthers()
    {
        var options = new FlightSyncOptions(); // Fast 2 min, Live 10 min, Fast window 4 h
        var fast = WithId(Flight.Create(1, "TK1", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(3), Now.AddHours(6)), 1);
        var live = WithId(Flight.Create(1, "TK2", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(30), Now.AddHours(33)), 2);
        var schedule = WithId(Flight.Create(1, "TK3", 10, 20, DateOnly.FromDateTime(Now.AddDays(20)), Now.AddDays(20), Now.AddDays(20).AddHours(3)), 3);
        var all = new[] { fast, live, schedule };
        foreach (var flight in all)
            flight.ApplySyncPlan(FlightSyncMode.Live, Now); // due now, as the migration backfill leaves them

        _flights.GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>()).Returns(_ => all
            .Where(f => f.NextSyncDueUtc <= _clock.GetUtcNow().UtcDateTime)
            .OrderBy(f => f.NextSyncDueUtc)
            .Select(f => f.Id)
            .ToList());
        _flights.GetFlightByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ci => all.Single(f => f.Id == ci.ArgAt<int>(0)));
        _flights.IsFlightTrackedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var provider = Substitute.For<IFlightStatusProvider>();
        provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var window = Substitute.For<IFlightStatusWindowOptions>();
        window.StatusWindowFutureDays.Returns(2);
        var handler = new SyncFlightStatusCommandHandler(_flights, provider, options, window, _clock,
            NullLogger<SyncFlightStatusCommandHandler>.Instance);
        _mediator.Send(Arg.Any<SyncFlightStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.ArgAt<SyncFlightStatusCommand>(0), CancellationToken.None).GetAwaiter().GetResult());

        var service = Service(options);
        for (var minute = 0; minute < 10; minute++)
        {
            await service.RunOnceAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        await provider.Received(5).GetStatusAsync("TK1", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()); // minutes 0, 2, 4, 6, 8
        await provider.Received(1).GetStatusAsync("TK2", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()); // minute 0 only
        await provider.DidNotReceive().GetStatusAsync("TK3", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        Assert.Single(Sent(), c => c.FlightId == 3); // planned once, next due when Live starts
    }
}
