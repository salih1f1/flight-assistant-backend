using FlightAssistant.Application.EventHandlers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Tests.Commands.Admin.Verification;
using FlightAssistant.Application.Tests.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Flights.Events;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlightAssistant.Application.Tests.EventHandlers;

/// <summary>
/// <see cref="FlightTrackedVerificationEventHandler"/> (#410 task 2). Every row starts, via the
/// constructor's baseline, freshly verified by a real source so a test only has to unsettle the one
/// row it is about -- an assertion that nothing else was queued is then a real assertion, not an
/// accident of an empty fixture.
/// </summary>
public class FlightTrackedVerificationEventHandlerTests
{
    private const int AirlineId = 1;
    private const int DepartureAirportId = 2;
    private const int ArrivalAirportId = 3;
    private const int FlightId = 100;
    private const int StaleAfterDays = 30;

    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAirlineRepository _airlines = Substitute.For<IAirlineRepository>();
    private readonly IAirportRepository _airports = Substitute.For<IAirportRepository>();
    private readonly ICountryRepository _countries = Substitute.For<ICountryRepository>();
    private readonly IVerificationQueue _queue = Substitute.For<IVerificationQueue>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly FakeJobOptions _options = new(StaleAfterDays: StaleAfterDays);
    private readonly IVerificationSourceDefaults _sourceDefaults = Substitute.For<IVerificationSourceDefaults>();

    // Costed like the real defaults (ourairports/mledoze free, aeroapi $0.015/row) so a test can prove
    // the queued cost really is summed from the registry rather than hardcoded.
    private readonly FakeVerificationSourceRegistry _registry = new(
        TestSources.Source("ourairports", 0m, VerificationEntityType.Airport),
        TestSources.Source("aeroapi", 0.015m, VerificationEntityType.Airline),
        TestSources.Source("mledoze", 0m, VerificationEntityType.Country));

    public FlightTrackedVerificationEventHandlerTests()
    {
        // Baseline: airline, both airports and both their countries all freshly verified by a real
        // source -- nothing here should be queued unless a test deliberately unsettles it.
        var airline = NewAirline(AirlineId, "GB");
        airline.Verify("admin", "ourairports", Now.AddDays(-1));
        _airlines.GetByIdAsync(AirlineId, Arg.Any<CancellationToken>()).Returns(airline);

        var departure = NewAirport(DepartureAirportId, "LHR", "GB");
        departure.Verify("admin", "ourairports", Now.AddDays(-1));
        _airports.GetByIdAsync(DepartureAirportId, Arg.Any<CancellationToken>()).Returns(departure);

        var arrival = NewAirport(ArrivalAirportId, "JFK", "US");
        arrival.Verify("admin", "ourairports", Now.AddDays(-1));
        _airports.GetByIdAsync(ArrivalAirportId, Arg.Any<CancellationToken>()).Returns(arrival);

        var gb = NewCountry("GB");
        gb.Verify("admin", "mledoze", Now.AddDays(-1));

        var us = NewCountry("US");
        us.Verify("admin", "mledoze", Now.AddDays(-1));

        // The handler resolves every distinct code in one batched call (R8) -- reply with whichever
        // of the two seeded countries the requested codes actually cover, the way a real WHERE
        // Code IN (...) would.
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var codes = ci.Arg<IReadOnlyCollection<string>>();
                return (IReadOnlyList<Country>)new[] { gb, us }.Where(c => codes.Contains(c.Code)).ToList();
            });

        _queue.EnqueueAsync(Arg.Any<VerificationEntityType>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<VerificationTrigger>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationEnqueueResult(Queued: 1, Skipped: 0));

        // Baseline: the switch mirrors Verification:DefaultSources:* for every type, exactly like
        // FakeJobOptions above -- a test that cares about the switch itself overrides one of these.
        _sourceDefaults.ForAsync(VerificationEntityType.Airport, Arg.Any<CancellationToken>())
            .Returns(new[] { "ourairports" });
        _sourceDefaults.ForAsync(VerificationEntityType.Airline, Arg.Any<CancellationToken>())
            .Returns(new[] { "aeroapi" });
        _sourceDefaults.ForAsync(VerificationEntityType.Country, Arg.Any<CancellationToken>())
            .Returns(new[] { "mledoze" });
    }

    private FlightTrackedVerificationEventHandler Handler() => new(
        _airlines, _airports, _countries, _queue, _registry, _options, _sourceDefaults, _clock,
        NullLogger<FlightTrackedVerificationEventHandler>.Instance);

    private static FlightTracked Event() =>
        new(FlightId, AirlineId, DepartureAirportId, ArrivalAirportId, Now);

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static TEntity WithId<TEntity>(TEntity entity, int id)
    {
        typeof(TEntity).GetProperty("Id")!.SetValue(entity, id);
        return entity;
    }

    private static Airline NewAirline(int id, string countryCode) =>
        WithId(Airline.Create("BA", "BAW", "British Airways", countryCode), id);

    private static Airport NewAirport(int id, string iata, string countryCode) =>
        WithId(Airport.Create(iata, null, iata + " Airport", "City", countryCode), id);

    private static Country NewCountry(string code) =>
        Country.Create(code, code + " Country");

    private Task ExpectNoCallFor(VerificationEntityType entityType) =>
        _queue.DidNotReceive().EnqueueAsync(entityType, Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<VerificationTrigger>(), Arg.Any<string>(),
            Arg.Any<decimal>(), Arg.Any<CancellationToken>());

    // ── R3/R4: what gets queued ────────────────────────────────────────────

    [Fact]
    public async Task Handle_UnverifiedAirline_IsQueued_WhileFreshlyVerifiedRowsAreNot()
    {
        var airline = NewAirline(AirlineId, "GB"); // never verified
        _airlines.GetByIdAsync(AirlineId, Arg.Any<CancellationToken>()).Returns(airline);

        await Handler().Handle(Event(), default);

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Airline,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 1 && k.Single() == "1"),
            Arg.Any<IReadOnlyCollection<string>>(), VerificationTrigger.Flight, VerificationJobActor.ActorId,
            Arg.Any<decimal>(), Arg.Any<CancellationToken>());

        // The baseline's airports and countries are freshly verified -- neither type is touched.
        await ExpectNoCallFor(VerificationEntityType.Airport);
        await ExpectNoCallFor(VerificationEntityType.Country);
    }

    [Fact]
    public async Task Handle_AirportVerifiedLongerAgoThanTheThreshold_IsQueued()
    {
        var departure = NewAirport(DepartureAirportId, "LHR", "GB");
        departure.Verify("admin", "ourairports", Now.AddDays(-(StaleAfterDays + 1)));
        _airports.GetByIdAsync(DepartureAirportId, Arg.Any<CancellationToken>()).Returns(departure);

        await Handler().Handle(Event(), default);

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Airport,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 1 && k.Single() == "2"),
            Arg.Any<IReadOnlyCollection<string>>(), VerificationTrigger.Flight, VerificationJobActor.ActorId,
            Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AirportVerifiedAtAFutureTimestamp_IsQueued()
    {
        // The imported-row case: a CSV import keeps the file's own VerifiedAt, which can be later than
        // "now" as far as this process is concerned -- that must not read as fresh forever.
        var arrival = NewAirport(ArrivalAirportId, "JFK", "US");
        arrival.Verify("admin", "ourairports", Now.AddDays(5));
        _airports.GetByIdAsync(ArrivalAirportId, Arg.Any<CancellationToken>()).Returns(arrival);

        await Handler().Handle(Event(), default);

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Airport,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 1 && k.Single() == "3"),
            Arg.Any<IReadOnlyCollection<string>>(), VerificationTrigger.Flight, VerificationJobActor.ActorId,
            Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AirportVerifiedMomentsInTheFuture_ClockSkewIsNotStale_IsNotQueued()
    {
        // M5/Fix 5: a few hundred milliseconds -- or even a couple of minutes -- ahead of "now" is
        // ordinary clock skew between App Service instances, not the imported-row case. It must not
        // cost a metered re-check.
        var arrival = NewAirport(ArrivalAirportId, "JFK", "US");
        arrival.Verify("admin", "ourairports", Now.AddMinutes(2));
        _airports.GetByIdAsync(ArrivalAirportId, Arg.Any<CancellationToken>()).Returns(arrival);

        await Handler().Handle(Event(), default);

        await ExpectNoCallFor(VerificationEntityType.Airport);
    }

    [Fact]
    public async Task Handle_AirlineVerifiedOnlyByImport_IsQueuedEvenThoughRecent()
    {
        var airline = NewAirline(AirlineId, "GB");
        airline.Verify("system", VerificationSources.Import, Now.AddDays(-1)); // recent, but import
        _airlines.GetByIdAsync(AirlineId, Arg.Any<CancellationToken>()).Returns(airline);

        await Handler().Handle(Event(), default);

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Airline,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 1 && k.Single() == "1"),
            Arg.Any<IReadOnlyCollection<string>>(), VerificationTrigger.Flight, VerificationJobActor.ActorId,
            Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AirportWithAnOpenIssue_IsSkipped_EvenThoughItAlsoLooksStale()
    {
        // MarkVerificationFailed leaves IsVerified false and VerifiedAt null -- which would read as
        // stale by R3 on its own -- but a non-blank VerificationIssue must win regardless (R4).
        var departure = NewAirport(DepartureAirportId, "LHR", "GB");
        departure.MarkVerificationFailed("Name, City differ (ourairports)");
        _airports.GetByIdAsync(DepartureAirportId, Arg.Any<CancellationToken>()).Returns(departure);

        await Handler().Handle(Event(), default);

        await ExpectNoCallFor(VerificationEntityType.Airport);
    }

    // ── R1 controller ruling: the open-request skip is the queue's, not the handler's ─────────────

    [Fact]
    public async Task Handle_QueueSkipsAnOpenRequest_TheHandlerStillPassesTheKeyThrough()
    {
        var airline = NewAirline(AirlineId, "GB"); // unverified -> needs a check
        _airlines.GetByIdAsync(AirlineId, Arg.Any<CancellationToken>()).Returns(airline);

        // Simulates the queue's own documented behaviour: the key's entity already has an open
        // request, so it comes back Skipped rather than Queued. The handler must not pre-filter this
        // itself, or duplicate the check -- it just passes the key through and moves on.
        _queue.EnqueueAsync(VerificationEntityType.Airline, Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<VerificationTrigger>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationEnqueueResult(Queued: 0, Skipped: 1));

        await Handler().Handle(Event(), default);

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Airline,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 1 && k.Single() == "1"),
            Arg.Any<IReadOnlyCollection<string>>(), VerificationTrigger.Flight, VerificationJobActor.ActorId,
            Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    // ── R6/R7: batching ─────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_EveryRowNeedsAChecking_QueuesEachEntityTypeOnceWithItsOwnSourcesTriggerAndCost()
    {
        var airline = NewAirline(AirlineId, "GB"); // unverified
        var departure = NewAirport(DepartureAirportId, "LHR", "GB"); // unverified
        var arrival = NewAirport(ArrivalAirportId, "JFK", "US"); // unverified
        _airlines.GetByIdAsync(AirlineId, Arg.Any<CancellationToken>()).Returns(airline);
        _airports.GetByIdAsync(DepartureAirportId, Arg.Any<CancellationToken>()).Returns(departure);
        _airports.GetByIdAsync(ArrivalAirportId, Arg.Any<CancellationToken>()).Returns(arrival);

        var gb = NewCountry("GB"); // unverified
        var us = NewCountry("US"); // unverified
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Country> { gb, us });

        await Handler().Handle(Event(), default);

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Airline,
            Arg.Is<IReadOnlyCollection<string>>(k => k.SequenceEqual(new[] { "1" })),
            Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "aeroapi" })),
            VerificationTrigger.Flight, VerificationJobActor.ActorId, 0.015m, Arg.Any<CancellationToken>());

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Airport,
            Arg.Is<IReadOnlyCollection<string>>(k => k.SequenceEqual(new[] { "2", "3" })),
            Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "ourairports" })),
            VerificationTrigger.Flight, VerificationJobActor.ActorId, 0m, Arg.Any<CancellationToken>());

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Country,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 2 && k.Contains("GB") && k.Contains("US")),
            Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "mledoze" })),
            VerificationTrigger.Flight, VerificationJobActor.ActorId, 0m, Arg.Any<CancellationToken>());

        // Never more than one call per type -- three in all, not one per row.
        await _queue.ReceivedWithAnyArgs(3).EnqueueAsync(default, default!, default!, default, default!, default, default);
    }

    // ── #531: the runtime switch ────────────────────────────────────────────

    [Fact]
    public async Task Handle_QueuesWithTheSwitchedSources_NotTheConfiguredOnes()
    {
        // The whole point of the switch: an admin routes airline checks to a free source and the
        // next track request queues against that source, with no restart.
        var airline = NewAirline(AirlineId, "GB"); // unverified -> needs a check
        _airlines.GetByIdAsync(AirlineId, Arg.Any<CancellationToken>()).Returns(airline);

        _sourceDefaults.ForAsync(VerificationEntityType.Airline, Arg.Any<CancellationToken>())
            .Returns(new[] { "ourairports" });

        await Handler().Handle(Event(), default);

        await _queue.Received(1).EnqueueAsync(
            VerificationEntityType.Airline,
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "ourairports" })),
            VerificationTrigger.Flight,
            VerificationJobActor.ActorId,
            Arg.Any<decimal>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DuplicateCountryCodesAcrossAirlineAndBothAirports_ProduceOneKeyNotThree()
    {
        // Airline, departure and arrival all resolve to GB -- the same country three different ways.
        var departure = NewAirport(DepartureAirportId, "LHR", "GB");
        departure.Verify("admin", "ourairports", Now.AddDays(-1)); // fresh: no Airport call from this
        var arrival = NewAirport(ArrivalAirportId, "MAN", "GB");
        arrival.Verify("admin", "ourairports", Now.AddDays(-1)); // fresh: no Airport call from this
        _airports.GetByIdAsync(DepartureAirportId, Arg.Any<CancellationToken>()).Returns(departure);
        _airports.GetByIdAsync(ArrivalAirportId, Arg.Any<CancellationToken>()).Returns(arrival);
        // The baseline airline is already GB and freshly verified -- no Airline call from it either.

        var gb = NewCountry("GB"); // unverified -> the only thing that should get queued
        _countries.FindByCodesAsync(
                Arg.Is<IReadOnlyCollection<string>>(c => c.Count == 1 && c.Single() == "GB"),
                Arg.Any<CancellationToken>())
            .Returns(new List<Country> { gb });

        await Handler().Handle(Event(), default);

        // One batched call with a single de-duplicated code -- not three calls, and not a call
        // carrying "US" at all (nothing on this fixture resolves to US).
        await _countries.Received(1).FindByCodesAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Count == 1 && c.Single() == "GB"),
            Arg.Any<CancellationToken>());

        await _queue.Received(1).EnqueueAsync(VerificationEntityType.Country,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 1 && k.Single() == "GB"),
            Arg.Any<IReadOnlyCollection<string>>(), VerificationTrigger.Flight, VerificationJobActor.ActorId,
            Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        await ExpectNoCallFor(VerificationEntityType.Airline);
        await ExpectNoCallFor(VerificationEntityType.Airport);
    }

    // ── R5: never throws ────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_TheAirlineRepositoryThrows_IsCaughtAndTheHandlerReturnsNormally()
    {
        _airlines.GetByIdAsync(AirlineId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        await Handler().Handle(Event(), default); // must not throw

        await _queue.DidNotReceiveWithAnyArgs()
            .EnqueueAsync(default, default!, default!, default, default!, default, default);
    }

    [Fact]
    public async Task Handle_TheQueueThrows_IsCaughtAndTheHandlerReturnsNormally()
    {
        var airline = NewAirline(AirlineId, "GB"); // unverified -> reaches EnqueueAsync
        _airlines.GetByIdAsync(AirlineId, Arg.Any<CancellationToken>()).Returns(airline);

        _queue.EnqueueAsync(VerificationEntityType.Airline, Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<VerificationTrigger>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("save failed"));

        await Handler().Handle(Event(), default); // must not throw
    }
}
