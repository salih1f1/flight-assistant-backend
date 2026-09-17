# Gated Verification-Source Test Deadline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop four verification-source tests from flaking on CI. The fix gives the tests that hold a gated download open a download deadline that no scheduling delay can reach, and makes any future regression fail on every run instead of only on a slow runner.

**Architecture:** This changes tests only. No production code changes. Each of the two test classes gets two constants: a long download deadline for its gated tests, and a deliberate gate hold longer than the 100 ms default deadline.

**Tech Stack:** .NET 10, xUnit.

**Spec:** issue #431, which records the root cause and a deterministic reproduction.

## Global Constraints

- **Change no production code.** Only `OurAirportsVerificationSourceTests.cs` and `MledozeCountriesVerificationSourceTests.cs` change (plus this plan).
- **Keep `TestTimeout` at 100 ms.** The tests that genuinely exercise the download deadline rely on it staying short.
- **Keep the existing test-level `.WaitAsync(TimeSpan.FromSeconds(5))` guards.** They make a genuine hang fail fast.
- In this solution only `--filter "FullyQualifiedName~…"` matches tests. `Name~` and `ClassName=` silently match nothing and still exit 0, so check the passed count is not zero.
- The build stays warning-free. Commits use explicit paths. Nothing is pushed.

---

## Root cause (from #431)

The tests' `TestTimeout` (100 ms) is passed to the source as its download deadline. `VerificationSourceDownloader.DownloadAsync` enforces it with `CancelAfter`, which is wall-clock time. The gated tests keep `GatedHandler` closed while they do other work, and `GatedHandler` ignores the cancellation token. On a loaded CI runner the gate can stay closed past 100 ms. The shared load then times out, and the joined caller gets `SourceError`, whose `Lookup` is null. That null is the `NullReferenceException` at the `IsFound` assert.

Reproduction: holding the gate for 250 ms against the 100 ms deadline fails every time; the same 250 ms hold with a 5 s deadline passes.

## File Structure

- **Modify** `FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/OurAirportsVerificationSourceTests.cs`
- **Modify** `FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/MledozeCountriesVerificationSourceTests.cs`

Each file has exactly two gated tests: `LookupAsync_TwoCallsThatBothMissTheCache_ShareOneDownload` and `LookupAsync_CancellingOneCallersRequest_DoesNotAffectAJoinedCaller`. Each file has exactly two `var source = Source(handler);` lines and two `handler.Release();` lines, one pair per gated test.

---

### Task 1: Give the gated tests their own deadline, and make a regression deterministic

**Files:** the two test files above.

- [ ] **Step 1: Add the two constants to each file**

Directly below the existing `private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(100);` line, add:

```csharp
    /// <summary>
    /// The download deadline for the tests that hold <see cref="GatedHandler"/> closed (#431).
    /// Those tests are about joining a shared download and per-caller cancellation, not about the
    /// deadline. With <see cref="TestTimeout"/>'s 100 ms, a loaded CI runner could keep the gate
    /// closed past it: the shared load then timed out, and the joined caller got a source error.
    /// </summary>
    private static readonly TimeSpan GatedDownloadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the gated tests deliberately keep the gate closed before releasing it: longer than
    /// <see cref="TestTimeout"/>, so a gated test that loses its own deadline fails on every run
    /// rather than only on a slow one (#431).
    /// </summary>
    private static readonly TimeSpan GateHold = TestTimeout * 3;
```

- [ ] **Step 2: Hold the gate first, and see all four tests fail**

In both gated tests of both files, replace

```csharp
        handler.Release();
```

with

```csharp
        await Task.Delay(GateHold);
        handler.Release();
```

Leave `Source(handler)` unchanged for now, then run:

```
dotnet test FlightAssistant.Infrastructure.Tests --filter "FullyQualifiedName~LookupAsync_CancellingOneCallersRequest_DoesNotAffectAJoinedCaller|FullyQualifiedName~LookupAsync_TwoCallsThatBothMissTheCache_ShareOneDownload"
```

Expected: **4 run, 4 fail.** This proves the hold turns the flaky failure into a deterministic one. If fewer than 4 fail, stop and report.

- [ ] **Step 3: Give the gated tests their deadline, and see them pass**

In the same four tests, replace

```csharp
        var source = Source(handler);
```

with

```csharp
        var source = Source(handler, downloadTimeout: GatedDownloadTimeout);
```

Run the same filter. Expected: **4 run, 4 pass.**

- [ ] **Step 4: Show it is stable**

Run the same filter **20 times** with `--no-build` after the first run. Every run must report 4 passed.

- [ ] **Step 5: Full check and commit**

```
dotnet test                                   # full suite; report per-project totals as observed
dotnet build --configuration Release          # 0 errors, 0 warnings
git diff --stat origin/development -- FlightAssistant.Infrastructure/ FlightAssistant.API/ FlightAssistant.Application/ FlightAssistant.Domain/ FlightAssistant.Contracts/   # must print NOTHING
```

```bash
git add FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/OurAirportsVerificationSourceTests.cs \
        FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/MledozeCountriesVerificationSourceTests.cs
git commit -m "test: give the gated verification-source tests their own download deadline (#431)"
```
