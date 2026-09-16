# Shared FlightStatusApplier Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the "record one provider status on a flight" logic out of `SyncFlightStatusCommandHandler` into a shared `FlightStatusApplier`, so Phase 2's alert webhook reuses it instead of copying it.

**Architecture:** A new Application service, `IFlightStatusApplier` / `FlightStatusApplier`, takes a `Flight` and an already-fetched `FlightStatusResult` and does everything after the provider call: resolve the status id (with the `UNK` fallback), compute the dedup hash, branch to the known-status path (#351), create the event, and let the flight decide the push (#352). The sync handler keeps everything around it — the traveler check, cooldown, budget guard, planner, the provider call itself, and `SaveChangesAsync`. Behaviour does not change.

**Tech Stack:** .NET 10, Clean Architecture + CQRS (MediatR), EF Core + Npgsql, xUnit, NSubstitute, FakeTimeProvider.

**Spec:** issue #386, and the Phase 2 section of `docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md` ("Extract that path into a shared `FlightStatusApplier`, not a copy").

## Global Constraints

- **Behaviour does not change.** No API change, no migration, no `spec/openapi.json` change, no configuration key.
- **`FlightStatusEvent.ComputeDedupHash`'s formula stays byte-for-byte.** `ComputeDedupHash_FormulaIsUnchanged` pins it at `47E102A0F36D99F2`. Every stored hash must keep matching.
- **The event's source stays the provider name** (`_provider.ProviderName`, `"AeroApi"` in production), passed into the applier. Change it and every stored hash stops matching.
- **The applier never calls `SaveChangesAsync`.** The caller owns the unit of work, so an alert and a poll can each pick their own transaction boundary.
- **The applier makes no provider call.** It receives a `FlightStatusResult` that the caller already fetched.
- **No paid API calls** (AeroAPI, AeroDataBox or any other metered service), no dev database access, and no `dotnet ef database update` while implementing this.
- **The existing `SyncFlightStatusCommandHandlerTests` assertions stay untouched.** They are the safety net proving behaviour didn't move. The only permitted edit to existing tests is adding the new constructor argument at the two places that construct the handler.
- **The build stays warning-free:** `dotnet build --configuration Release` reports 0 errors, 0 warnings.
- **Commits** use explicit paths (never `-A` or `.`), and each message ends with a blank line, then the attribution trailer the implementer's own instructions give.

---

## File Structure

- **Create** `FlightAssistant.Application/FlightSync/IFlightStatusApplier.cs` — the interface, beside `IFlightSyncOptions.cs` and `FlightSyncPlanner.cs`, which is where flight-sync collaborators already live.
- **Create** `FlightAssistant.Application/FlightSync/FlightStatusApplier.cs` — the implementation, holding the code moved out of the handler.
- **Modify** `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs` — take the new dependency, call it, and delete the moved members.
- **Modify** `FlightAssistant.Infrastructure/DependencyInjection.cs` — register it beside `IProviderBudgetGuard`, which is where Application services are registered in this repo.
- **Modify** `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs` and `FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs` — the two places that construct the handler.
- **Create** `FlightAssistant.Application.Tests/FlightSync/FlightStatusApplierTests.cs` — direct tests at the new seam (Task 2).
- **Modify** `CLAUDE.md` — document the seam (Task 2).

---

### Task 1: Extract the applier and delegate to it

**Files:**
- Create: `FlightAssistant.Application/FlightSync/IFlightStatusApplier.cs`
- Create: `FlightAssistant.Application/FlightSync/FlightStatusApplier.cs`
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs`
- Modify: `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs` (constructor call only)
- Modify: `FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs` (constructor call only)

**Interfaces:**
- Consumes: `IFlightRepository` (`GetFlightStatusIdByCodeAsync`, `FlightStatusEventExistsAsync`, `GetLatestFlightStatusEventAsync`, `AddFlightStatusEventAsync`), `FlightStatusResult`, `Flight.ApplyStatusUpdate` / `ApplyProviderTimes`, `FlightStatusEvent.Create` / `CreateDetailChange` / `ComputeDedupHash`.
- Produces: `IFlightStatusApplier.ApplyAsync(Flight flight, FlightStatusResult result, string source, DateTime nowUtc, CancellationToken ct = default)` returning `Task<bool>` — true when an event was recorded. Task 2's tests and Phase 2's alert command both consume this exact signature.

- [ ] **Step 1: Run the existing suites and record they're green**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"`
Expected: PASS. These assertions are the safety net for the whole task — record the count in the report.

- [ ] **Step 2: Create the interface**

Create `FlightAssistant.Application/FlightSync/IFlightStatusApplier.cs`:

```csharp
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Application.FlightSync
{
    /// <summary>
    /// Records one provider status on a flight: the status row id (with the <c>UNK</c> fallback), the
    /// dedup hash, the event, and the flight's own push decision (#351, #352). Shared so that every way a
    /// status arrives stores the same thing by the same rules — the polling sync today, and Phase 2's
    /// alert webhook (#364) next — rather than each keeping a copy of these rules.
    /// </summary>
    public interface IFlightStatusApplier
    {
        /// <summary>
        /// Applies an already-fetched status. Makes no provider call and never saves: the caller owns the
        /// unit of work, so a poll and an alert can each choose their own transaction boundary.
        /// </summary>
        /// <param name="source">The provider name recorded on the event and hashed into its dedup hash.
        /// It must stay the same string for a given provider, or stored hashes stop matching.</param>
        /// <returns>Whether an event was recorded.</returns>
        Task<bool> ApplyAsync(
            Flight flight, FlightStatusResult result, string source, DateTime nowUtc, CancellationToken ct = default);
    }
}
```

- [ ] **Step 3: Create the implementation, moving the code verbatim**

Create `FlightAssistant.Application/FlightSync/FlightStatusApplier.cs`. The three members move out of `SyncFlightStatusCommandHandler` unchanged except that `_provider.ProviderName` becomes the `source` parameter and the fetch happens in the caller:

```csharp
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.FlightSync
{
    /// <inheritdoc cref="IFlightStatusApplier"/>
    public sealed class FlightStatusApplier : IFlightStatusApplier
    {
        // The safe, always-seeded fallback code (see FlightStatusConfiguration.HasData) that
        // every provider maps its own unrecognised statuses to. An unknown code reaching this far
        // means a *provider* has emitted something outside that mapping — not a data problem — so
        // it degrades to the same fallback rather than aborting the sync.
        private const string UnknownStatusCode = "UNK";

        private readonly IFlightRepository _flights;
        private readonly ILogger<FlightStatusApplier> _logger;

        public FlightStatusApplier(IFlightRepository flights, ILogger<FlightStatusApplier> logger)
        {
            _flights = flights;
            _logger = logger;
        }

        /// <summary>
        /// A new status becomes an event, and the flight decides whether it pushes (#352); a status the
        /// flight already has goes to <see cref="ApplyKnownStatusAsync"/> (#351). Changes the flight only
        /// after every step that can throw, so a failure leaves nothing half-applied for the caller to save.
        /// </summary>
        public async Task<bool> ApplyAsync(
            Flight flight, FlightStatusResult result, string source, DateTime nowUtc, CancellationToken ct = default)
        {
            // Look up the FlightStatus row id first, so the dedup hash uses the same id-based
            // formula as the stored hash (see FlightStatusEvent.ComputeDedupHash).
            //
            // Every provider already maps its own unrecognised statuses to "UNK" (the seeded safe
            // fallback — see FlightStatusConfiguration.HasData), so a code that still fails to
            // resolve here means a provider has started emitting something outside that mapping.
            // That is a provider change to investigate, not a reason to lose this flight's status
            // update entirely (and discard the AeroAPI call already spent fetching it) — so this
            // degrades to "UNK" and logs a warning naming the code, rather than aborting.
            var resolvedStatusCode = result.StatusCode;
            var statusId = await _flights.GetFlightStatusIdByCodeAsync(resolvedStatusCode, ct);
            if (statusId is null)
            {
                _logger.LogWarning(
                    "Unrecognised flight status code {StatusCode} from provider {Provider} for flight {FlightId}; falling back to {Fallback}",
                    result.StatusCode, source, flight.Id, UnknownStatusCode);

                resolvedStatusCode = UnknownStatusCode;
                statusId = await _flights.GetFlightStatusIdByCodeAsync(UnknownStatusCode, ct)
                    ?? throw new DomainException(
                        $"Flight status code '{UnknownStatusCode}' is not seeded; the flight_statuses reference table is misconfigured.",
                        404);
            }

            var dedupHash = FlightStatusEvent.ComputeDedupHash(flight.Id, statusId.Value, result.EffectiveAtUtc, source, result.ExternalId);
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct))
                return await ApplyKnownStatusAsync(flight, statusId.Value, result, source, nowUtc, ct);

            // The flight's current status is its most recently recorded event (#351). Whether the status changed
            // is one input to the push decision the flight makes (#352): a corrected actual departure, or a delay
            // that hasn't grown 15 minutes past the last delay push, records the event without a push. Read
            // before the flight changes.
            var latest = await _flights.GetLatestFlightStatusEventAsync(flight.Id, ct);

            var evt = FlightStatusEvent.Create(
                flight.Id,
                statusId.Value,
                result.EffectiveAtUtc,
                source,
                result.ExternalId,
                result.Gate,
                result.Terminal,
                result.BaggageClaim,
                rawPayloadJson: result.RawPayloadJson);

            // ApplyStatusUpdate raises FlightStatusChanged, which the DomainEventDispatchInterceptor
            // publishes after SaveChanges succeeds.
            flight.ApplyStatusUpdate(
                resolvedStatusCode,
                result.EstimatedDepartureUtc,
                result.EstimatedArrivalUtc,
                result.ActualDepartureUtc,
                result.ActualArrivalUtc,
                result.EquipmentIcao,
                nowUtc,
                statusChanged: latest?.StatusId != statusId.Value,
                reportedScheduledDepartureUtc: result.ScheduledDepartureUtc);

            await _flights.AddFlightStatusEventAsync(evt, ct);
            return true;
        }

        /// <summary>
        /// The provider reported a status this flight already has (#351). The response used to be dropped.
        /// Now its times update the flight, and a newly reported gate, terminal or baggage claim is recorded
        /// as a detail-change event. Neither raises <c>FlightStatusChanged</c>, so neither sends a push. A
        /// detail the provider no longer reports keeps its last known value. A detail change is recorded
        /// only when the reported status is the latest event's. Returns whether an event was
        /// recorded. The flight changes only after the latest-event read, the step that can throw.
        /// </summary>
        private async Task<bool> ApplyKnownStatusAsync(
            Flight flight, int statusId, FlightStatusResult result, string source, DateTime nowUtc, CancellationToken ct)
        {
            var latest = await _flights.GetLatestFlightStatusEventAsync(flight.Id, ct);

            flight.ApplyProviderTimes(result.EstimatedDepartureUtc, result.EstimatedArrivalUtc,
                result.ActualDepartureUtc, result.ActualArrivalUtc, result.EquipmentIcao, nowUtc);

            // Only on top of the same status as the latest event. A response whose status matches an older
            // event but not the latest (a status that returned, or an out-of-order response) records nothing,
            // as before: a detail change under that status would relabel the current status with no push and
            // no CancelledAtUtc update.
            if (latest is null || latest.StatusId != statusId || !ReportsNewDetails(result, latest))
                return false;

            await _flights.AddFlightStatusEventAsync(FlightStatusEvent.CreateDetailChange(
                flight.Id, statusId, result.EffectiveAtUtc, source, result.ExternalId, latest.Id,
                gate: result.Gate ?? latest.Gate,
                terminal: result.Terminal ?? latest.Terminal,
                baggageClaim: result.BaggageClaim ?? latest.BaggageClaim,
                rawPayloadJson: result.RawPayloadJson), ct);
            return true;
        }

        /// <summary>A detail is new only when the provider reports a value that differs from the latest event's.</summary>
        private static bool ReportsNewDetails(FlightStatusResult result, FlightStatusEvent latest) =>
            (result.Gate is not null && result.Gate != latest.Gate)
            || (result.Terminal is not null && result.Terminal != latest.Terminal)
            || (result.BaggageClaim is not null && result.BaggageClaim != latest.BaggageClaim);
    }
}
```

- [ ] **Step 4: Take the dependency in the handler**

In `SyncFlightStatusCommand.cs`:

1. Add `using FlightAssistant.Application.FlightSync;` if it isn't already there (it is — `FlightSyncPlanner` comes from that namespace).
2. Delete the `UnknownStatusCode` constant and its comment. Leave `TravelerAttemptKeyPrefix` alone.
3. Add the field `private readonly IFlightStatusApplier _applier;` after `_provider`, add the constructor parameter `IFlightStatusApplier applier` after `IFlightStatusProvider provider`, and assign it.

- [ ] **Step 5: Call the applier and delete the moved members**

Replace the whole `ApplyLiveStatusAsync` method, and the `ApplyKnownStatusAsync` and `ReportsNewDetails` methods that follow it, with this one method (the provider call stays inside the caller's `try`, so a failed call still defers the next sync):

```csharp
        /// <summary>
        /// Fetches the live status and hands it to <see cref="IFlightStatusApplier"/>, which records it and
        /// decides the push (#351, #352). Returns whether an event was recorded; a response with no matching
        /// row records nothing.
        /// </summary>
        private async Task<bool> ApplyLiveStatusAsync(Flight flight, DateTime now, StatusCallKind kind, CancellationToken ct)
        {
            var result = await _provider.GetStatusAsync(flight.FlightNumber, flight.ServiceDateUtc, kind, ct);
            if (result is null) return false;

            return await _applier.ApplyAsync(flight, result, _provider.ProviderName, now, ct);
        }
```

Check afterwards that `DomainException` and `FlightStatusEvent` are still used elsewhere in the file; if a `using` is now unused, remove it so the build stays warning-free.

- [ ] **Step 6: Register it**

In `FlightAssistant.Infrastructure/DependencyInjection.cs`, beside `services.AddScoped<IProviderBudgetGuard, ProviderBudgetGuard>();` (line ~260), add:

```csharp
            // Shared by the status sync and, from Phase 2 of #364, the alert webhook (#386). Scoped: it
            // writes through the request's own IFlightRepository and never saves.
            services.AddScoped<IFlightStatusApplier, FlightStatusApplier>();
```

Add `using FlightAssistant.Application.FlightSync;` if the file doesn't already have it.

- [ ] **Step 7: Fix the two handler construction sites**

Only the constructor argument changes; no assertion may change.

1. `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs` (it already has `using FlightAssistant.Application.FlightSync;` on line 2). The helper builds a real applier on the same `_flights` substitute, so the tests keep exercising the real apply rules. Replace

```csharp
    private SyncFlightStatusCommandHandler Handler(
        ILogger<SyncFlightStatusCommandHandler>? logger = null, IFlightSyncOptions? syncOptions = null,
        ICacheService? cache = null) =>
        new(_flights, _provider, _budget, cache ?? _cache, syncOptions ?? new SyncOptions(), new WindowOptions(), _clock,
            logger ?? NullLogger<SyncFlightStatusCommandHandler>.Instance);
```

with

```csharp
    private SyncFlightStatusCommandHandler Handler(
        ILogger<SyncFlightStatusCommandHandler>? logger = null, IFlightSyncOptions? syncOptions = null,
        ICacheService? cache = null, ILogger<FlightStatusApplier>? applierLogger = null) =>
        new(_flights, _provider,
            new FlightStatusApplier(_flights, applierLogger ?? NullLogger<FlightStatusApplier>.Instance),
            _budget, cache ?? _cache, syncOptions ?? new SyncOptions(), new WindowOptions(), _clock,
            logger ?? NullLogger<SyncFlightStatusCommandHandler>.Instance);
```

2. **The one test that reads the fallback warning** is `UnrecognisedStatusCode_FallsBackToUnk_AndLogsWarning_RatherThanThrowing` (around line 655). The warning is now written by the applier's logger, so the capturing logger has to be the applier's. Replace its two lines

```csharp
        var logger = new CapturingLogger<SyncFlightStatusCommandHandler>();
        var handler = Handler(logger);
```

with

```csharp
        // The fallback warning is the applier's now (#386); the assertion below is unchanged.
        var logger = new CapturingLogger<FlightStatusApplier>();
        var handler = Handler(applierLogger: logger);
```

The `Assert.Contains(logger.Entries, …)` at the end of that test, and every other line in it, stay exactly as they are. `CapturingLogger<T>` is already generic, so no change is needed to the helper class. **No other test in this file may change** — the two `CapturingLogger<SyncFlightStatusCommandHandler>` uses around lines 497 and 511 are about the cooldown marker, which stays in the handler.

3. `FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs` (around line 191): add the applier as the third argument, keeping everything else as is.

```csharp
        var handler = new SyncFlightStatusCommandHandler(_flights, provider,
            new FlightStatusApplier(_flights, NullLogger<FlightStatusApplier>.Instance), budget,
            Substitute.For<ICacheService>(), options, window, _clock,
            NullLogger<SyncFlightStatusCommandHandler>.Instance);
```

Add `using FlightAssistant.Application.FlightSync;` to that file if it isn't already there.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: every project PASS, with the same counts as Step 1 for the handler tests.
Then run: `dotnet build --configuration Release` — 0 warnings, 0 errors.

- [ ] **Step 9: Prove the moved code is really in the path**

Each break, then restore with `mv … .bak` and `touch`. Record the failing output.

1. In `FlightStatusApplier.ApplyAsync`, make the `UNK` fallback throw instead of degrading (`statusId = null` path): the handler test for an unrecognised code fails.
2. In `FlightStatusApplier.ApplyAsync`, pass `statusChanged: true` unconditionally: `NewStatus_TheSameStatusAsTheLatestEvent_RecordsIt_WithoutAPush` fails.
3. In `ApplyKnownStatusAsync`, return `false` before recording the detail change: `KnownStatus_ANewGate_RecordsADetailChange_WithNoPush` fails.

If any of these three passes, the handler is not going through the applier — stop and report it.

- [ ] **Step 10: Commit**

```bash
git add FlightAssistant.Application/FlightSync/IFlightStatusApplier.cs FlightAssistant.Application/FlightSync/FlightStatusApplier.cs FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs FlightAssistant.Infrastructure/DependencyInjection.cs FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs
git commit -m "refactor: extract the status-apply path into a shared FlightStatusApplier (#386)"
```

---

### Task 2: Direct tests at the new seam, and document it

**Files:**
- Create: `FlightAssistant.Application.Tests/FlightSync/FlightStatusApplierTests.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: `FlightStatusApplier.ApplyAsync` exactly as Task 1 produced it.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the direct tests**

Create `FlightAssistant.Application.Tests/FlightSync/FlightStatusApplierTests.cs`. These test the applier on its own, without the sync handler's planner, cooldown or budget around it — which is how Phase 2's alert command will call it:

```csharp
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
```

- [ ] **Step 2: Run them**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~FlightStatusApplierTests"`
Expected: PASS, 7 tests.

- [ ] **Step 3: Prove each test bites**

Break, watch the named test fail, restore with `mv … .bak` and `touch`. Record each failure.

1. Record the event's source as a literal `"AeroApi"` instead of `source` → `TheSourceReachesTheEventAndItsHash` fails.
2. Skip the `FlightStatusEventExistsAsync` check (always take the new-status path) → `AStatusTheFlightAlreadyHas_WithNothingNew_RecordsNothing` fails.
3. In `ReportsNewDetails`, compare only the gate's nullness (`result.Gate is not null`) → `AStatusTheFlightAlreadyHas_WithNothingNew_RecordsNothing` fails.
4. Call `_flights.SaveChangesAsync(ct)` at the end of `ApplyAsync` → `ANewStatus_RecordsTheEvent_AndNeverSaves` fails.

- [ ] **Step 4: Document the seam in CLAUDE.md**

In the Key Patterns section, immediately after the paragraph beginning `**A push goes out when a status travelers hear changes, or a delay grows (#352).**`, add:

```markdown
**Recording a status is one shared path (#386).** `FlightStatusApplier` (Application, `FlightSync/`) takes a `Flight` and an already-fetched `FlightStatusResult` and does everything after the provider call: resolving the status code to a row id with the `UNK` fallback, `ComputeDedupHash`, the known-status branch (#351), creating the `FlightStatusEvent`, and `Flight.ApplyStatusUpdate`, which decides the push (#352). `SyncFlightStatusCommandHandler` keeps what surrounds it — the traveler check, the manual-sync cooldown (#369), the budget guard (#303), the planner and the failure deferral (#356), the provider call itself, and `SaveChangesAsync`. It's a separate service because Phase 2 of #364 adds a second way a status arrives (an AeroAPI alert webhook), and a copy of these rules would drift: the dedup formula must never change, and the #351 and #352 rules are what decide whether a traveler's phone buzzes. **The applier never saves** — the caller owns the unit of work — and it makes no provider call, so it can't spend money. The `source` string it records and hashes must stay the provider's own name, since every stored hash is built from it.
```

- [ ] **Step 5: Run everything and commit**

Run: `dotnet test` (all four projects PASS) and `dotnet build --configuration Release` (0 warnings).
Run: `FlightSync__Enabled=false scripts/generate-openapi.sh && git diff --stat spec/openapi.json` — must print nothing.

```bash
git add FlightAssistant.Application.Tests/FlightSync/FlightStatusApplierTests.cs CLAUDE.md
git commit -m "test: cover FlightStatusApplier directly, and document the shared apply path (#386)"
```
