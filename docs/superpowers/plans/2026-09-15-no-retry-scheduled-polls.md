# No Retries for Scheduled Status Polls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A background status poll that hits a transient AeroAPI failure costs one billed call, not up to three. Searches, timezone lookups, the usage report and syncs a person triggers keep their retries (#375).

**Architecture:**
- **The mark (Infrastructure).** `AeroApiRetry` marks a single `HttpRequestMessage` as "skip retries". The one `"AeroApi"` client's retry predicate reads that mark from the resilience context and declines to retry, alongside today's never-retry-a-429 rule.
- **The intent (Application).** `IFlightStatusProvider.GetStatusAsync` gains an explicit `StatusCallKind`. `SyncFlightStatusCommandHandler` passes `ScheduledPoll` for `SyncTrigger.Scheduled` and `Interactive` otherwise. `AeroApiFlightStatusProvider` marks only a scheduled poll's request.
- **No second client.** The throttle, the circuit breaker and the derived timeouts stay shared.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Http.Resilience` 10.6.0 (Polly v8), MediatR, xUnit, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md` (Phase 1, item 5) and issue #375.

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-375`, branch `feature/375-no-retry-scheduled-polls`. Never touch the main checkout or other worktrees.
- One named `"AeroApi"` client and one resilience pipeline. No second named client.
  - `MaxRetryAttempts`, the derived timeouts, the circuit breaker and the throttle don't change.
  - A 429 is still never retried, and every other transient outcome is still retried for an unmarked request.
- Only a scheduled status poll is marked.
- **The provider API.** `IFlightStatusProvider.GetStatusAsync(string flightNumber, DateOnly serviceDate, StatusCallKind kind, CancellationToken ct = default)`, with `public enum StatusCallKind { Interactive, ScheduledPoll }` in `FlightAssistant.Application.Interfaces.ExternalServices`.
- **One home for the mark.** It is set and read only through `FlightAssistant.Infrastructure.ExternalServices.AeroApi.AeroApiRetry`.
- No API, DTO, schema, migration or `spec/openapi.json` change.
- Make no call to AeroAPI or any other paid external API. Every test uses stub transports or substitutes. Never run the API with sync on. Never run `dotnet ef database update`.
- Tests with option values use **non-default** values, so a hardcoded constant can't pass (#308).
- The build stays warning-free (#301).
- TDD: every new test is seen failing first. Each task ends with a prove step: break the code on purpose, watch the named test fail, restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- Commit with explicit paths only (never `-A` or `.`). Every commit message ends with a blank line and `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Never push.

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-375
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiClientPipelineTests"
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightStatusProviderTests"
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightStatusSyncBackgroundServiceTests"
```

---

### Task 1: A request can opt out of the `"AeroApi"` client's retries

**Files:**
- Create: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiRetry.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs` (the `options.Retry.ShouldHandle` predicate)
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiClientPipelineTests.cs`

**Interfaces:**
- Produces: `public static class AeroApiRetry` with `public static void Skip(HttpRequestMessage request)` and `public static bool IsSkipped(HttpRequestMessage? request)`. Task 2's provider calls `Skip`, and Task 2's provider tests call `IsSkipped`.

- [ ] **Step 1: Write the failing tests** in `AeroApiClientPipelineTests`.

(a) Make `CountingTransport` configurable. Existing `new CountingTransport()` callers keep answering 200:

```csharp
    /// <summary>Counts what actually made it past every handler to the wire. Answers with
    /// <paramref name="status"/>, or throws a network error when <paramref name="networkError"/> is set.</summary>
    private sealed class CountingTransport(HttpStatusCode status = HttpStatusCode.OK, bool networkError = false) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (networkError)
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
```

(b) Give `BuildContainer` an optional `int? maxRetryAttempts = null` parameter that sets `settings["AeroApi:MaxRetryAttempts"]`. Existing callers don't change.

(c) Add these tests. Each uses a non-default `MaxRetryAttempts` of 1, so an unmarked request reaches the transport twice.

```csharp
    // ── Opting a request out of retries (#375) ──

    [Fact]
    public async Task ATransientStatus_IsRetried_UpToMaxRetryAttempts()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport(HttpStatusCode.ServiceUnavailable);
        using var provider = BuildContainer(clock, transport, maxRetryAttempts: 1);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        var pending = client.GetAsync("flights/TK1028");
        await AdvanceUntilSettled(clock, pending, TimeSpan.FromMinutes(5));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await pending).StatusCode);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task ATransientStatus_IsNotRetried_WhenTheRequestSkipsRetries()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport(HttpStatusCode.ServiceUnavailable);
        using var provider = BuildContainer(clock, transport, maxRetryAttempts: 1);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        using var request = new HttpRequestMessage(HttpMethod.Get, "flights/TK1028");
        AeroApiRetry.Skip(request);
        var pending = client.SendAsync(request);
        await AdvanceUntilSettled(clock, pending, TimeSpan.FromMinutes(5));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await pending).StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task ANetworkError_IsRetried_UpToMaxRetryAttempts()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport(networkError: true);
        using var provider = BuildContainer(clock, transport, maxRetryAttempts: 1);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        var pending = client.GetAsync("flights/TK1028");
        await AdvanceUntilSettled(clock, pending, TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<HttpRequestException>(() => pending);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task ANetworkError_IsNotRetried_WhenTheRequestSkipsRetries()
    {
        // A network error has no response, so the predicate can't find the request through
        // args.Outcome.Result — it must read it from the resilience context.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var transport = new CountingTransport(networkError: true);
        using var provider = BuildContainer(clock, transport, maxRetryAttempts: 1);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi");

        using var request = new HttpRequestMessage(HttpMethod.Get, "flights/TK1028");
        AeroApiRetry.Skip(request);
        var pending = client.SendAsync(request);
        await AdvanceUntilSettled(clock, pending, TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<HttpRequestException>(() => pending);
        Assert.Equal(1, transport.Calls);
    }
```

- [ ] **Step 2: Run, expect a compile failure**, because `AeroApiRetry` doesn't exist.
  - Then add an empty `AeroApiRetry` whose `Skip` does nothing and whose `IsSkipped` returns `false`, and run again.
  - Expected: both `…_IsNotRetried_WhenTheRequestSkipsRetries` tests FAIL (2 calls), and both `…_IsRetried_UpToMaxRetryAttempts` tests pass. The retried tests are guards, proven by break 3 below.

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiClientPipelineTests"`

- [ ] **Step 3: Implement**

`AeroApiRetry.cs`:

```csharp
namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Marks a single outbound AeroAPI request so the <c>"AeroApi"</c> client's resilience pipeline
    /// doesn't retry it (#375). A scheduled status poll uses it: AeroAPI bills failed calls, and the
    /// sync loop's next due sync is already the retry. Setting and reading the mark both live here,
    /// so the key can't drift between the provider and the pipeline.
    /// </summary>
    public static class AeroApiRetry
    {
        private static readonly HttpRequestOptionsKey<bool> SkipKey = new("AeroApi.SkipRetry");

        public static void Skip(HttpRequestMessage request) => request.Options.Set(SkipKey, true);

        public static bool IsSkipped(HttpRequestMessage? request) =>
            request is not null && request.Options.TryGetValue(SkipKey, out var skip) && skip;
    }
}
```

In `DependencyInjection.cs`, extend the retry predicate and its comment:

```csharp
                // 429 must never be retried automatically: this is a $5/month metered tier, and
                // retrying a rate-limit response only makes the throttling worse. A request marked
                // with AeroApiRetry.Skip isn't retried either — a scheduled status poll, whose next
                // due sync is its retry (#375). The request is read from the resilience context,
                // because a network error has no response to find it on. All other transient outcomes
                // (5xx, timeouts, network errors) keep the default handling.
                options.Retry.ShouldHandle = args =>
                {
                    if (args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests)
                        return ValueTask.FromResult(false);

                    if (AeroApiRetry.IsSkipped(args.Context.GetRequestMessage()))
                        return ValueTask.FromResult(false);

                    return ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome));
                };
```

`GetRequestMessage()` is `Microsoft.Extensions.Http.Resilience`'s extension on Polly's `ResilienceContext` (namespace `Polly`). If it doesn't compile against 10.6.0 or returns null inside the retry predicate, stop and report. Don't fall back to `args.Outcome.Result?.RequestMessage` alone, because `ANetworkError_IsNotRetried_WhenTheRequestSkipsRetries` exists to catch exactly that.

- [ ] **Step 4: Run, expect PASS**, then run the whole `FlightAssistant.Infrastructure.Tests` project. Docker must be running; its PostgreSQL tests use Testcontainers.

- [ ] **Step 5: Prove** (restore each with `mv … .bak` and `touch`)
  1. Remove the `AeroApiRetry.IsSkipped` check from the predicate: both `…_IsNotRetried_WhenTheRequestSkipsRetries` tests fail.
  2. Read the request only from the outcome (`AeroApiRetry.IsSkipped(args.Outcome.Result?.RequestMessage)`): `ANetworkError_IsNotRetried_WhenTheRequestSkipsRetries` fails, and `ATransientStatus_IsNotRetried_WhenTheRequestSkipsRetries` still passes.
  3. Hardcode `options.Retry.MaxRetryAttempts = 2;`: both `…_IsRetried_UpToMaxRetryAttempts` tests fail (3 calls).

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiRetry.cs FlightAssistant.Infrastructure/DependencyInjection.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiClientPipelineTests.cs
git commit -m "feat: let an AeroAPI request opt out of the client's retries (#375)"
```

---

### Task 2: A scheduled status poll opts out; everything else keeps retrying

**Files:**
- Modify: `FlightAssistant.Application/Interfaces/ExternalServices/IFlightStatusProvider.cs`
- Modify: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightStatusProvider.cs`
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs` (`Handle`, `ApplyLiveStatusAsync`)
- Test: `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs`, `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightStatusProviderTests.cs`, `FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: Task 1's `AeroApiRetry.Skip` and `AeroApiRetry.IsSkipped`.
- Produces: `StatusCallKind` and the new `GetStatusAsync` signature.

- [ ] **Step 1: Write the failing tests**

(a) **Interface first, so the tests compile against it.** In `IFlightStatusProvider.cs`, add the enum and change the method:

```csharp
    /// <summary>Who a status call is for (#375). A scheduled poll isn't retried on a transient
    /// failure, because AeroAPI bills failed calls and the sync loop's next due sync is the retry. A call a
    /// person is waiting on keeps the provider's retries.</summary>
    public enum StatusCallKind
    {
        Interactive,
        ScheduledPoll
    }

    public interface IFlightStatusProvider
    {
        string ProviderName { get; }
        Task<FlightStatusResult?> GetStatusAsync(string flightNumber, DateOnly serviceDate, StatusCallKind kind, CancellationToken ct = default);
    }
```

(b) **Update every existing call site mechanically** (69 of them):
- In `SyncFlightStatusCommandHandlerTests`, add `Arg.Any<StatusCallKind>()` as the new third argument to every `_provider.GetStatusAsync(...)` setup, `Received` and `DidNotReceive`.
- In `FlightStatusSyncBackgroundServiceTests`:
  - add `Arg.Any<StatusCallKind>()` to the `.ReturnsNull()` setup;
  - add `StatusCallKind.ScheduledPoll` to the three `Received`/`DidNotReceive` assertions on `"TK1"`, `"TK2"` and `"TK3"`. Every loop sync is a scheduled poll, so this pins it end to end.
- In `AeroApiFlightStatusProviderTests`, add `StatusCallKind.Interactive` to every `GetStatusAsync("…", …)` call.

Don't change any other argument or expected count.

(c) **New handler tests** in `SyncFlightStatusCommandHandlerTests`:

```csharp
    // ── Retry intent (#375) ──

    [Fact]
    public async Task ScheduledSync_CallsTheProvider_AsAScheduledPoll()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Scheduled);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, StatusCallKind.ScheduledPoll, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SyncTrigger.Admin)]
    [InlineData(SyncTrigger.Traveler)]
    public async Task PersonSync_CallsTheProvider_AsInteractive(SyncTrigger trigger)
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(trigger);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, StatusCallKind.Interactive, Arg.Any<CancellationToken>());
    }
```

The Traveler case uses a never-synced flight, so neither the cooldown nor the attempt marker (#369) skips the call.

(d) **New provider tests** in `AeroApiFlightStatusProviderTests`.
- Capture the mark inside the stub handler's `SendAsync`, e.g. a `LastRequestSkippedRetries` property set from `AeroApiRetry.IsSkipped(request)`. Reuse or extend the file's existing stub handler; don't add a second handler class if the existing one can record it.
- Add:
  - `GetStatusAsync_MarksAScheduledPoll_SoTheClientDoesNotRetryIt`: call with `StatusCallKind.ScheduledPoll`, assert the mark was set.
  - `GetStatusAsync_LeavesAnInteractiveCall_Retryable`: call with `StatusCallKind.Interactive`, assert it wasn't.
- Use the file's existing successful-response fixture, so the call completes normally.

- [ ] **Step 2: Run, expect a compile failure** in `AeroApiFlightStatusProvider` and `SyncFlightStatusCommandHandler`, because they don't implement or call the new signature yet.

Run: `dotnet build`

- [ ] **Step 3: Implement**

`AeroApiFlightStatusProvider.GetStatusAsync`:

```csharp
        public async Task<FlightStatusResult?> GetStatusAsync(
            string flightNumber, DateOnly serviceDate, StatusCallKind kind, CancellationToken ct = default)
        {
            var ident = flightNumber.ToUpperInvariant();
            var criteria = $"{ident} {serviceDate:yyyy-MM-dd}";

            // A scheduled poll isn't retried: failed calls are billed, and the loop's next due sync is
            // the retry (#375). A call a person is waiting on keeps the client's retries.
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(ident));
            if (kind == StatusCallKind.ScheduledPoll)
                AeroApiRetry.Skip(request);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
```

Leave the rest of the method unchanged.

`SyncFlightStatusCommandHandler`:
- Give `ApplyLiveStatusAsync` a `StatusCallKind kind` parameter before `CancellationToken ct`, and pass it to `_provider.GetStatusAsync(flight.FlightNumber, flight.ServiceDateUtc, kind, ct)`.
- At its call site in `Handle`, pass:

```csharp
cmd.Trigger == SyncTrigger.Scheduled ? StatusCallKind.ScheduledPoll : StatusCallKind.Interactive
```

- [ ] **Step 4: Run, expect PASS** for the four filtered commands above.

- [ ] **Step 5: Prove** (restore each with `mv … .bak` and `touch`)
  1. Handler always passes `StatusCallKind.Interactive`: `ScheduledSync_CallsTheProvider_AsAScheduledPoll` and `FlightStatusSyncBackgroundServiceTests.OverTenMinutes_AFlightNearDeparture_DoesNotSpeedUpTheOthers` fail.
  2. Handler always passes `StatusCallKind.ScheduledPoll`: both cases of `PersonSync_CallsTheProvider_AsInteractive` fail.
  3. Provider never calls `AeroApiRetry.Skip`: `GetStatusAsync_MarksAScheduledPoll_SoTheClientDoesNotRetryIt` fails.

- [ ] **Step 6: CLAUDE.md** — in the `**Flight data provider:**` paragraph, after the sentence ending `since retrying a throttle response on a metered tier only makes it worse.`, add:

```markdown
A scheduled status poll isn't retried either (#375): `SyncFlightStatusCommandHandler` passes `StatusCallKind.ScheduledPoll` for a `Scheduled` sync, `AeroApiFlightStatusProvider` marks that request with `AeroApiRetry.Skip`, and the retry predicate skips marked requests, because AeroAPI bills failed calls and the loop's next due sync is the retry. Searches, timezone lookups, the usage report and syncs a person triggers keep their retries.
```

- [ ] **Step 7: Full verification**

```bash
dotnet build --configuration Release 2>&1 | tail -3     # 0 errors, 0 warnings
dotnet test 2>&1 | grep -E "^(Passed|Failed)!|error"      # all four projects pass; record per-project counts
FlightSync__Enabled=false scripts/generate-openapi.sh && git diff --stat spec/openapi.json   # expect no diff
```

- [ ] **Step 8: Commit**

```bash
git add FlightAssistant.Application/Interfaces/ExternalServices/IFlightStatusProvider.cs \
  FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightStatusProvider.cs \
  FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs \
  FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightStatusProviderTests.cs \
  FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs CLAUDE.md
git commit -m "feat: a scheduled status poll isn't retried; the next due sync is its retry (#375)"
```

- [ ] **Step 9: Stop.** Don't push. The controller runs the final review and opens the PR.
