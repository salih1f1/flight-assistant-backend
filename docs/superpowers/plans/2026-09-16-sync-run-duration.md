# Sync Run Duration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record how long each flight-status sync run takes, warn when a run overruns its loop interval, and show the duration on `/admin/health`.

**Architecture:** `FlightStatusSyncBackgroundService` already owns a `TimeProvider`; it measures each run and passes the elapsed time to `FlightSyncHeartbeat`, which stays a plain state holder with no clock of its own. The admin health report reads it from the same single snapshot it already reads.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, xUnit, NSubstitute, `FakeTimeProvider`, `WebApplicationFactory`.

**Spec:** issue #353, narrowed by its own comment — #356 already fixed the delay-after-batch and the arbitrary ordering, and `RunBatchAsync`/`GetActiveTrackedFlightsAsync` no longer exist. What remains is "nothing shows how long a run takes", plus the warning the issue asks for when a run overruns.

## Global Constraints

- **Only the heartbeat and the health endpoint change.** No scheduling change: `PeriodicTimer` already starts the next run immediately after an overrun, and due flights are already ordered. Don't touch `RunOnceAsync`'s behaviour.
- **The duration is measured with the service's injected `TimeProvider`,** never `DateTime.UtcNow` or `Stopwatch`, so a `FakeTimeProvider` test can assert an exact value.
- **`FlightSyncHeartbeat` gains no clock and no constructor parameter.** Every existing `new FlightSyncHeartbeat()` — in `FlightStatusSyncBackgroundServiceTests`, `FlightSyncHeartbeatTests` and `AdminHealthEndpointTests` — must keep compiling untouched.
- **A run that has never completed reports `null`**, not zero. "Never run" must not read as "instant".
- **`FlightSync:Enabled = false` still leaves the heartbeat at `NeverRun`** with null timestamps and a null duration: disabled, not broken.
- **No new configuration key.** The overrun comparison uses the existing `FlightSync:LoopInterval` (default 1 minute, range 10 s–10 min).
- **`spec/openapi.json` is regenerated, never hand-edited** (`FlightSync__Enabled=false scripts/generate-openapi.sh` from the repo root), and the regenerated file is committed.
- **The build stays warning-free:** `dotnet build --configuration Release` reports 0 errors, 0 warnings.
- **Commits** use explicit paths (never `-A` or `.`), and each message ends with a blank line, then the attribution trailer the implementer's own instructions give.
- No paid API calls, no dev database access, no migration.

---

## File Structure

- **Modify** `FlightAssistant.API/HostedServices/FlightSyncHeartbeat.cs` — the snapshot gains `LastRunDuration`; the two `Mark*` completion methods accept it.
- **Modify** `FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs` — measure the run, pass it, warn on overrun.
- **Modify** `FlightAssistant.Contracts/Responses/Admin/AdminHealthDto.cs` — `AdminHealthFlightSyncDto` gains `LastRunDurationSeconds`.
- **Modify** `FlightAssistant.API/Endpoints/Admin/AdminHealthReportBuilder.cs` — map it from the snapshot.
- **Modify** the three test files named in the tasks, plus `spec/openapi.json` (regenerated) and `CLAUDE.md`.

---

### Task 1: Measure the run and record it on the heartbeat

**Files:**
- Modify: `FlightAssistant.API/HostedServices/FlightSyncHeartbeat.cs`
- Modify: `FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs`
- Modify: `FlightAssistant.API.Tests/HostedServices/FlightSyncHeartbeatTests.cs`
- Modify: `FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: `FlightSyncSnapshot`, `FlightSyncHeartbeat.MarkRunSucceeded/MarkRunFailed`, `FlightStatusSyncBackgroundService.RunAndRecordAsync`, `FlightSyncOptions.LoopInterval`.
- Produces: `FlightSyncSnapshot.LastRunDuration` (`TimeSpan?`, null until a run completes). Task 2 reads exactly this.

- [ ] **Step 1: Add the field to the snapshot**

In `FlightSyncHeartbeat.cs`, add a last positional parameter to the record, with its own doc line:

```csharp
    public sealed record FlightSyncSnapshot(
        DateTime? LastRunStartedUtc,
        DateTime? LastRunCompletedUtc,
        FlightSyncOutcome LastOutcome,
        int FlightsProcessed,
        int FlightsFailed,
        DateTime? NextRunDueUtc,
        string? LastError,
        // How long the last completed run took (#353). Null until one completes — a run that has
        // never finished must not read as an instant one. Measured by the background service with
        // its own TimeProvider, so this stays a plain state holder with no clock.
        TimeSpan? LastRunDuration = null);
```

The heartbeat's initial `_snapshot` uses named arguments, so it needs no change.

- [ ] **Step 2: Accept the duration on the two completion methods**

Give both an optional trailing parameter, so every existing call site keeps compiling:

```csharp
        public void MarkRunSucceeded(int processed, int failed, DateTime nextRunDueUtc, TimeSpan? duration = null)
```

and inside its `with`, add `LastRunDuration = duration,`. Then the same for failure:

```csharp
        public void MarkRunFailed(Exception ex, DateTime nextRunDueUtc, TimeSpan? duration = null)
```

also adding `LastRunDuration = duration,` inside its `with`. A failed run's duration is still worth reporting — a run that failed after ten minutes is a different problem from one that failed at once.

Extend `MarkRunSucceeded`'s summary with: `<paramref name="duration"/> is how long the run took, measured by the caller's clock.`

- [ ] **Step 3: Write the failing heartbeat tests**

In `FlightSyncHeartbeatTests.cs`, add:

```csharp
    [Fact]
    public void MarkRunSucceeded_RecordsTheRunDuration()
    {
        var heartbeat = new FlightSyncHeartbeat();

        heartbeat.MarkRunStarted();
        heartbeat.MarkRunSucceeded(processed: 4, failed: 0, nextRunDueUtc: DateTime.UtcNow.AddMinutes(1),
            duration: TimeSpan.FromSeconds(90));

        Assert.Equal(TimeSpan.FromSeconds(90), heartbeat.Current.LastRunDuration);
    }

    [Fact]
    public void MarkRunFailed_RecordsTheRunDuration()
    {
        var heartbeat = new FlightSyncHeartbeat();

        heartbeat.MarkRunStarted();
        heartbeat.MarkRunFailed(new InvalidOperationException("boom"), DateTime.UtcNow.AddMinutes(1),
            duration: TimeSpan.FromSeconds(12));

        Assert.Equal(TimeSpan.FromSeconds(12), heartbeat.Current.LastRunDuration);
    }

    [Fact]
    public void Current_BeforeAnyRun_HasNoDuration()
    {
        // Null, not zero: a run that has never completed must not read as an instant one.
        Assert.Null(new FlightSyncHeartbeat().Current.LastRunDuration);
    }
```

- [ ] **Step 4: Run them**

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightSyncHeartbeatTests"`
Expected: the three new tests PASS (Steps 1–2 already added the field), every existing test in the class still PASSES.

- [ ] **Step 5: Measure the run in the service**

In `FlightStatusSyncBackgroundService.RunAndRecordAsync`, replace the whole method body with:

```csharp
            _heartbeat.MarkRunStarted();
            var startedUtc = _timeProvider.GetUtcNow();
            var nextRunDueUtc = startedUtc.UtcDateTime + _options.LoopInterval;

            try
            {
                var result = await RunOnceAsync(ct);
                _heartbeat.MarkRunSucceeded(result.Processed, result.Failed, nextRunDueUtc, Elapsed(startedUtc));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Flight status sync run failed. Retrying in {Interval}.", _options.LoopInterval);
                _heartbeat.MarkRunFailed(ex, nextRunDueUtc, Elapsed(startedUtc));
            }
```

and add this private method directly after it:

```csharp
        /// <summary>
        /// How long the run took, by the injected clock rather than a <c>Stopwatch</c>, so a test on a
        /// <c>FakeTimeProvider</c> can assert an exact value (#353). A run longer than the loop interval is
        /// not an error — <c>PeriodicTimer</c> simply starts the next one immediately — but it means each
        /// flight is polled less often than the interval suggests, which is invisible without this warning.
        /// </summary>
        private TimeSpan Elapsed(DateTimeOffset startedUtc)
        {
            var elapsed = _timeProvider.GetUtcNow() - startedUtc;
            if (elapsed > _options.LoopInterval)
                _log.LogWarning(
                    "Flight status sync run took {Duration}, longer than the {Interval} loop interval; the next run starts immediately.",
                    elapsed, _options.LoopInterval);

            return elapsed;
        }
```

Note `nextRunDueUtc` now derives from the same `startedUtc` reading rather than a second clock call — same value, one source.

- [ ] **Step 6: Write the failing service tests**

In `FlightStatusSyncBackgroundServiceTests.cs`, add these next to the existing `StartAsync` tests. Use the file's existing `Service()` helper and its `WaitUntil` helper; make the mediator stub advance `_clock` so the run takes measurable time:

```csharp
    [Fact]
    public async Task ARun_RecordsHowLongItTook()
    {
        // The stub advances the fake clock while the run is in flight, so the recorded duration is
        // exactly what the clock moved — no wall-clock timing in the assertion.
        DueIds(1);
        _mediator
            .Send(Arg.Any<SyncFlightStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _clock.Advance(TimeSpan.FromSeconds(20)); return Task.FromResult(true); });

        var service = Service();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => _heartbeat.Current.LastOutcome == FlightSyncOutcome.Success);
            Assert.Equal(TimeSpan.FromSeconds(20), _heartbeat.Current.LastRunDuration);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ARunLongerThanTheLoopInterval_LogsAWarning()
    {
        DueIds(1);
        _mediator
            .Send(Arg.Any<SyncFlightStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _clock.Advance(TimeSpan.FromMinutes(5)); return Task.FromResult(true); });

        var logger = new CapturingLogger<FlightStatusSyncBackgroundService>();
        var service = Service(logger: logger);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => _heartbeat.Current.LastOutcome == FlightSyncOutcome.Success);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("longer than"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
```

**Two helper changes this needs** (both verified against the file at HEAD):

1. **`Service()` takes no logger today** — its signature is `private FlightStatusSyncBackgroundService Service(FlightSyncOptions? options = null)` and it passes `NullLogger<FlightStatusSyncBackgroundService>.Instance`. Add an optional trailing parameter and use it, keeping the default behaviour identical for every existing caller:

```csharp
    private FlightStatusSyncBackgroundService Service(
        FlightSyncOptions? options = null, ILogger<FlightStatusSyncBackgroundService>? logger = null)
```

passing `logger ?? NullLogger<FlightStatusSyncBackgroundService>.Instance` where the hardcoded instance is now.

2. **This project has no `CapturingLogger<T>`** (it exists only in `FlightAssistant.Application.Tests` and `FlightAssistant.Infrastructure.Tests`). Add a private one to this test class, copying the shape used in `SyncFlightStatusCommandHandlerTests`:

```csharp
    /// <summary>Records every log call, so a test can assert a warning was actually emitted.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
```

**On the mediator stub:** the snippets above use `.Returns(_ => { _clock.Advance(…); return Task.FromResult(true); })`. Match the form the file already uses for `_mediator.Send(...)` (see the existing stub around line 196) — if it returns a bare value, write `.Returns(_ => { _clock.Advance(…); return true; })` instead. The point is only that the clock advances while the run is in flight. `DueIds(int[])` is the existing helper for stubbing due flights, and `WaitUntil(Func<bool>)` already exists. Report whatever you adapted.

- [ ] **Step 7: Run the suite**

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj`
Expected: PASS, including every pre-existing test.

- [ ] **Step 8: Prove each new test bites**

Break, watch the named test fail, then restore with `mv … .bak` then `touch`. Record the break, the failing output and the restore.

1. Pass `duration: null` from `MarkRunSucceeded`'s call site in the service → `ARun_RecordsHowLongItTook` fails.
2. Compare with `>=` … actually, drop the overrun `if` entirely → `ARunLongerThanTheLoopInterval_LogsAWarning` fails.
3. Default `LastRunDuration` to `TimeSpan.Zero` instead of `null` in the record → `Current_BeforeAnyRun_HasNoDuration` fails.

- [ ] **Step 9: Commit**

```bash
git add FlightAssistant.API/HostedServices/FlightSyncHeartbeat.cs FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs FlightAssistant.API.Tests/HostedServices/FlightSyncHeartbeatTests.cs FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs
git commit -m "feat: record how long each flight sync run takes, and warn when one overruns its interval (#353)"
```

---

### Task 2: Show it on /admin/health

**Files:**
- Modify: `FlightAssistant.Contracts/Responses/Admin/AdminHealthDto.cs`
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminHealthReportBuilder.cs`
- Modify: `FlightAssistant.API.Tests/Integration/AdminHealthEndpointTests.cs`
- Modify: `spec/openapi.json` (regenerated, never hand-edited)
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: `FlightSyncSnapshot.LastRunDuration` from Task 1.
- Produces: `AdminHealthFlightSyncDto.LastRunDurationSeconds` (`double?`), serialised as `lastRunDurationSeconds`.

- [ ] **Step 1: Add the field to the DTO**

In `AdminHealthDto.cs`, add a last positional parameter to `AdminHealthFlightSyncDto`:

```csharp
    public sealed record AdminHealthFlightSyncDto(
        bool Enabled,
        DateTime? LastRunStartedUtc,
        DateTime? LastRunCompletedUtc,
        string LastOutcome,
        int FlightsProcessed,
        int FlightsFailed,
        DateTime? NextRunDueUtc,
        string? LastError,
        double? LastRunDurationSeconds
    );
```

and add this `<param>` line to the record's existing doc comment, after the `LastError` one:

```csharp
    /// <param name="LastRunDurationSeconds">How long the most recently completed run took, in seconds, or
    /// <c>null</c> when none has completed — "never run", not "instant". Compare it with
    /// <c>FlightSync:LoopInterval</c>: a run that takes longer means each flight is polled less often than
    /// the interval suggests, because the next run starts immediately rather than on the tick (#353).</param>
```

Seconds rather than a `TimeSpan`, because a `TimeSpan` serialises as `"00:01:30"` and a panel would have to parse it.

- [ ] **Step 2: Map it**

In `AdminHealthReportBuilder.BuildFlightSync`, add the last argument:

```csharp
                includeDiagnostics ? snapshot.LastError : null,
                snapshot.LastRunDuration?.TotalSeconds);
```

Not gated by `includeDiagnostics`: a duration carries no exception detail, and Production is exactly where an operator needs it.

- [ ] **Step 3: Write the failing endpoint test**

In `AdminHealthEndpointTests.cs`, add a test near the existing flight-sync ones, using the file's own factory helpers. Model it on `WithFailedFlightSyncHeartbeat`, which already registers a pre-populated `FlightSyncHeartbeat` singleton:

```csharp
    [Fact]
    public async Task GetHealth_ReportsTheLastRunDuration()
    {
        var heartbeat = new FlightSyncHeartbeat();
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunSucceeded(processed: 3, failed: 0, nextRunDueUtc: DateTime.UtcNow.AddMinutes(1),
            duration: TimeSpan.FromSeconds(42));

        using var scopedFactory = WithFlightSyncHeartbeat(factory, heartbeat);
        var client = scopedFactory.CreateClient();

        var dto = await (await client.GetAsync("/admin/health")).Content.ReadFromJsonAsync<AdminHealthDto>();

        Assert.Equal(42, dto!.FlightSync.LastRunDurationSeconds);
    }

    [Fact]
    public async Task GetHealth_BeforeAnyRun_HasNoRunDuration()
    {
        var dto = await (await factory.CreateClient().GetAsync("/admin/health")).Content.ReadFromJsonAsync<AdminHealthDto>();

        Assert.Null(dto!.FlightSync.LastRunDurationSeconds);
    }
```

**`WithFlightSyncHeartbeat` does not exist yet** — verified at HEAD, the only helper is:

```csharp
    private static WebApplicationFactory<Program> WithFailedFlightSyncHeartbeat(
        FlightAssistantWebApplicationFactory factory, string? environmentName = null)
```

which builds its own failed heartbeat inside. Generalise it: add a helper that takes a prepared heartbeat, and have the failed-run one delegate to it so no existing test changes.

```csharp
    /// <summary>Registers a prepared <see cref="FlightSyncHeartbeat"/> as the singleton, overriding
    /// Program.cs's own registration (DI resolves the last descriptor). The integration factory removes
    /// every <c>IHostedService</c>, so the background service that would call <c>Mark*</c> never runs in
    /// this suite — this stands in for it.</summary>
    private static WebApplicationFactory<Program> WithFlightSyncHeartbeat(
        FlightAssistantWebApplicationFactory factory, FlightSyncHeartbeat heartbeat, string? environmentName = null) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                if (environmentName is not null)
                    services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));

                services.AddSingleton(heartbeat);
            }));
```

Then `WithFailedFlightSyncHeartbeat` keeps its signature and body down to building the failed heartbeat, ending in `return WithFlightSyncHeartbeat(factory, heartbeat, environmentName);`. Its XML doc stays — move the parts describing the registration technique onto the new helper if that reads better. Don't change what any existing test asserts. `factory` is the test class's primary-constructor parameter, and `ReadFromJsonAsync<AdminHealthDto>()` is the established way these tests read the response.

- [ ] **Step 4: Run the tests**

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~AdminHealthEndpointTests"`
Expected: PASS, including every pre-existing test in the class.

- [ ] **Step 5: Regenerate the spec**

From the repo root:

```bash
FlightSync__Enabled=false scripts/generate-openapi.sh
git diff --stat spec/openapi.json
```

Expected: exactly one schema changes — `AdminHealthFlightSyncDto` gains `lastRunDurationSeconds`. If anything else moves, stop and report it rather than committing it.

- [ ] **Step 6: Prove the new tests bite**

Break, watch the named test fail, restore with `mv … .bak` then `touch`. Record each.

1. Map `null` instead of `snapshot.LastRunDuration?.TotalSeconds` in `BuildFlightSync` → `GetHealth_ReportsTheLastRunDuration` fails.
2. Map `snapshot.LastRunDuration?.TotalMinutes` → the same test fails (42 vs 0.7).

- [ ] **Step 7: Document it**

In `CLAUDE.md`, in the paragraph beginning `**`FlightStatusSyncBackgroundService` publishes a heartbeat.**`, add after its last sentence:

```markdown
It also records how long each run took (#353) — `FlightSyncSnapshot.LastRunDuration`, surfaced as `flightSync.lastRunDurationSeconds` on `/admin/health`, null until a run completes so "never run" cannot read as "instant". The background service measures it with its injected `TimeProvider`, not a `Stopwatch`, so a `FakeTimeProvider` test asserts an exact value, and the heartbeat stays a plain state holder with no clock. A run longer than `FlightSync:LoopInterval` logs a warning: `PeriodicTimer` starts the next run immediately, so nothing breaks, but each flight is then polled less often than the interval suggests — which is invisible without it.
```

- [ ] **Step 8: Full check and commit**

Run: `dotnet test` (all four projects PASS) and `dotnet build --configuration Release` (0 warnings, 0 errors).

```bash
git add FlightAssistant.Contracts/Responses/Admin/AdminHealthDto.cs FlightAssistant.API/Endpoints/Admin/AdminHealthReportBuilder.cs FlightAssistant.API.Tests/Integration/AdminHealthEndpointTests.cs spec/openapi.json CLAUDE.md
git commit -m "feat: show the last sync run's duration on /admin/health (#353)"
```
