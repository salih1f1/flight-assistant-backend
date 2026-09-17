# Heartbeat Test Single-Read Wait Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `ACycleThatPaused_MarksTheHeartbeatPaused_NotSuccessOrFailed` from passing its wait while the cycle is still `Running`. The test's wait condition reads the heartbeat twice; after this change it reads once per poll and asserts on the exact snapshot that ended the wait.

**Architecture:** Tests only. Add one helper, `WaitForSnapshotAsync`, to `ReferenceVerificationBackgroundServiceTests`, and use it in the one test whose condition reads `Heartbeat.Current` twice. The other six `WaitUntilAsync` uses each read one terminal outcome once, so they stay as they are.

**Tech Stack:** .NET 10, xUnit, `FakeTimeProvider`.

**Spec:** issue #438, which has the root cause, the forced-interleaving reproduction, and the scope analysis of all seven waits.

## Global Constraints

- **No production code changes.** Only `FlightAssistant.API.Tests/HostedServices/ReferenceVerificationBackgroundServiceTests.cs` changes (plus this plan).
- **Change only the one test** and add the one helper. Leave `WaitUntilAsync` and the other six tests alone.
- **Temporary experiment edits (Steps 1 and 3) must be fully reverted.** Before committing, `git diff` must show only the helper and the test change.
- Only `--filter "FullyQualifiedName~…"` matches in this solution. `Name~` and `ClassName=` silently match nothing and still exit 0, so check the count every time.
- Diff production code against the **merge base**, not `origin/development`. `development` may move while you work.
- Build stays warning-free; commits use explicit paths; nothing is pushed.

---

## Root cause (from #438)

```csharp
await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.Running
    && job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.NeverRun);
var snapshot = job.Heartbeat.Current;
```

Each `Current` access can return a newer snapshot, and the background loop runs on another thread. When read 1 sees `NeverRun` and the loop then publishes `Running` before read 2, both halves of the condition are true while the cycle is still running. The post-wait read then returns `Running`.

---

### Task 1: Read the heartbeat once per poll, and assert on the snapshot that ended the wait

**File:** `FlightAssistant.API.Tests/HostedServices/ReferenceVerificationBackgroundServiceTests.cs`

**Test filter used throughout:** `FullyQualifiedName~ReferenceVerificationBackgroundServiceTests.ACycleThatPaused_MarksTheHeartbeatPaused_NotSuccessOrFailed`. It must report exactly **1** test.

- [ ] **Step 1: Reproduce red with a forced interleaving, then revert**

Make two temporary edits to the test as it is now.

1. Slow the cycle so `Running` lasts. Replace

```csharp
        script.Then(() => Processed(processed: 5, failed: 1, paused: 3));
```

with

```csharp
        script.ThenAsync(async _ => { await Task.Delay(300); return Processed(processed: 5, failed: 1, paused: 3); }); // EXPERIMENT
```

2. Force the loop to start between the two reads. Replace the two-line condition

```csharp
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.Running
            && job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.NeverRun);
```

with

```csharp
        await WaitUntilAsync(() =>
        {
            var firstPasses = job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.Running; // EXPERIMENT: read 1
            SpinWait.SpinUntil(() => job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.NeverRun, TimeSpan.FromSeconds(5));
            return firstPasses && job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.NeverRun; // read 2
        });
```

Build once, then run the filter **10 times** with `--no-build`. Expected: **10/10 fail** with `Expected: Paused, Actual: Running`. If any run passes, stop and report.

Revert both edits with `git checkout -- <file>`, and confirm `git diff` is empty.

- [ ] **Step 2: Add the helper and use it**

Directly below the existing `WaitUntilAsync` method, add:

```csharp
    /// <summary>
    /// Polls <paramref name="heartbeat"/> until one snapshot satisfies <paramref name="predicate"/>, and
    /// returns that snapshot (#438). It reads <see cref="VerificationJobHeartbeat.Current"/> exactly once
    /// per poll. Each access can return a newer snapshot, so a condition that reads it twice can pass on
    /// two different states, and re-reading it after the wait can return a later state.
    /// </summary>
    private static async Task<VerificationJobSnapshot> WaitForSnapshotAsync(
        VerificationJobHeartbeat heartbeat, Func<VerificationJobSnapshot, bool> predicate)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (true)
        {
            var snapshot = heartbeat.Current;
            if (predicate(snapshot)) return snapshot;
            if (DateTime.UtcNow > deadline) Assert.Fail("The background service did not reach the expected state in time.");
            await Task.Delay(10);
        }
    }
```

In `ACycleThatPaused_MarksTheHeartbeatPaused_NotSuccessOrFailed`, replace

```csharp
        await WaitUntilAsync(() => job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.Running
            && job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.NeverRun);

        var snapshot = job.Heartbeat.Current;
```

with

```csharp
        // One read per poll, and the asserts use the snapshot that ended the wait (#438). The old
        // condition read Current twice, so NeverRun on the first read and Running on the second
        // satisfied both halves while the cycle was still running.
        var snapshot = await WaitForSnapshotAsync(job.Heartbeat,
            s => s.LastOutcome is not (VerificationJobOutcome.Running or VerificationJobOutcome.NeverRun));
```

Run the filter once. Expected: 1 passed.

- [ ] **Step 3: Green under the same forced interleaving, then revert the experiment**

Make two temporary edits to the **fixed** test.

1. The same slow-cycle edit as in Step 1.
2. Force the same interleaving inside the predicate. Replace the lambda

```csharp
            s => s.LastOutcome is not (VerificationJobOutcome.Running or VerificationJobOutcome.NeverRun));
```

with

```csharp
            s =>
            {
                SpinWait.SpinUntil(() => job.Heartbeat.Current.LastOutcome != VerificationJobOutcome.NeverRun, TimeSpan.FromSeconds(5)); // EXPERIMENT
                return s.LastOutcome is not (VerificationJobOutcome.Running or VerificationJobOutcome.NeverRun);
            });
```

Build once, then run the filter **10 times** with `--no-build`. Expected: **10/10 pass**. A stale `NeverRun` snapshot is simply rejected, and a later poll reads `Paused`.

Revert **only** these experiment edits. Afterwards, `git diff` must show exactly the Step 2 change and nothing marked `EXPERIMENT`. Check with `grep -n EXPERIMENT <file>`, which must print nothing.

- [ ] **Step 4: Stability**

Run the whole class 30 times with `--no-build`, using `--filter "FullyQualifiedName~HostedServices.ReferenceVerificationBackgroundServiceTests"`. Every run must be fully green, with the same test count each time. Report the tally.

- [ ] **Step 5: Full check and commit**

```
dotnet test                                   # full suite; per-project totals as observed
dotnet build --configuration Release          # 0 errors, 0 warnings
git diff --stat $(git merge-base HEAD origin/development) -- FlightAssistant.API/ FlightAssistant.Infrastructure/ FlightAssistant.Application/ FlightAssistant.Domain/ FlightAssistant.Contracts/   # must print NOTHING
```

```bash
git add FlightAssistant.API.Tests/HostedServices/ReferenceVerificationBackgroundServiceTests.cs
git commit -m "test: read the verification heartbeat once per poll in the paused-cycle test (#438)"
```
