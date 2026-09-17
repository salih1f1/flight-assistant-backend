# Verification-Source Test Deadline Default Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop any verification-source test from flaking on a slow runner. Invert the default download deadline, so tests get a long one unless they exist to watch the deadline fire.

**Architecture:** Tests only; no production code changes. In each of the two test classes:
- `TestTimeout` becomes 30 s.
- A new `ShortDeadline` (100 ms) is passed explicitly by the two tests that need the deadline to fire.
- #432's `GatedDownloadTimeout` is removed, since the default now covers it.
- `GateHold` is re-based on `ShortDeadline`.
- A new regression test proves an ordinary lookup survives a response slower than `ShortDeadline`.

**Tech Stack:** .NET 10, xUnit.

**Spec:** issue #433, which holds the root cause, a deterministic reproduction, and the list of tests that genuinely need a short deadline. It corrects #431, whose root cause was too narrow.

## Global Constraints

- **No production code changes.** Only `OurAirportsVerificationSourceTests.cs` and `MledozeCountriesVerificationSourceTests.cs` change, plus this plan.
- **Exactly two tests per class keep a short deadline:** `LookupAsync_ReturnsASourceError_WhenNothingAnswers` and `LookupAsync_ReturnsASourceError_WhenTheBodyStalls`. Keep their existing `.WaitAsync(TimeSpan.FromSeconds(5))` guards.
- **`GateHold` must stay about 300 ms.** It is currently `TestTimeout * 3`, and after the inversion that expression would make it **90 seconds**.
- Only `--filter "FullyQualifiedName~…"` matches tests in this solution. `Name~` and `ClassName=` silently match nothing and still exit 0, so check the count on every run.
- The build stays warning-free. Commits use explicit paths. Nothing is pushed.

---

## Root cause (from #433)

`TestTimeout` (100 ms) is the source's download deadline. `VerificationSourceDownloader.DownloadAsync` enforces it with wall-clock `CancelAfter`, and it covers **send and parse together**. A slow or cold runner can exceed 100 ms on an ordinary lookup, which then returns `SourceError`. That happened in deploy run `35207326873`: `LookupAsync_MapsAllEightColumns_ForAMatchingIataRow`, `Assert.False(IsError)` failed after 135 ms, with no gate involved.

Reproduction: a handler that answers after 150 ms fails that test against the 100 ms default and passes against a 5 s deadline.

## File Structure

- **Modify** `FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/OurAirportsVerificationSourceTests.cs`
- **Modify** `FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/MledozeCountriesVerificationSourceTests.cs`

---

### Task 1: Invert the default deadline, keeping a short one only where it must fire

**Files:** the two test files above.

- [ ] **Step 1: Add the regression test first, and see it fail**

In **each** file, add a handler next to the existing `StaticHandler`. For `OurAirportsVerificationSourceTests.cs`, the content type is `"text/csv"`:

```csharp
    /// <summary>Answers like <see cref="StaticHandler"/>, but only after <paramref name="delay"/> (#433).</summary>
    private sealed class SlowStaticHandler(HttpStatusCode status, string body, TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/csv") };
        }
    }
```

For `MledozeCountriesVerificationSourceTests.cs`, use the same class with content type `"application/json"`. Match that file's own `StaticHandler`.

Then add the new constant `ShortDeadline` to both files, directly below `TestTimeout`, and **do not change `TestTimeout` yet**:

```csharp
    /// <summary>
    /// A deadline short enough to fire quickly, for the tests that exist to watch it fire:
    /// <c>LookupAsync_ReturnsASourceError_WhenNothingAnswers</c> and
    /// <c>LookupAsync_ReturnsASourceError_WhenTheBodyStalls</c>. Pass it explicitly, and never make
    /// it the default again (#433).
    /// </summary>
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(100);
```

Now add the regression test in **each** file:

```csharp
    [Fact]
    public async Task LookupAsync_Succeeds_WhenTheResponseIsSlowerThanTheShortDeadline()
    {
        // #433: the default download deadline covers send AND parse, on the wall clock. At 100 ms a
        // slow CI runner timed out ordinary lookups with no gate involved. This response is slower
        // than that on purpose, so a default that shrinks back fails here on every run.
        var source = Source(new SlowStaticHandler(HttpStatusCode.OK, <FIXTURE>, ShortDeadline * 3));

        var result = await source.LookupAsync(<ENTITY TYPE>, <KEY>)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsError);
        Assert.True(result.Lookup!.IsFound);
    }
```

Fill the three placeholders from each file's **own** existing passing lookup test:
- `OurAirportsVerificationSourceTests`: `FixtureCsv`, `VerificationEntityType.Airport`, `VerificationSourceLookupKey.ForAirport("LHR", null)`. These match `LookupAsync_MapsAllEightColumns_ForAMatchingIataRow`.
- `MledozeCountriesVerificationSourceTests`: use the fixture constant, entity type and a key that an existing test in that file already asserts `IsFound` for. Read the file to find them.

Run:

```
dotnet test FlightAssistant.Infrastructure.Tests --filter "FullyQualifiedName~LookupAsync_Succeeds_WhenTheResponseIsSlowerThanTheShortDeadline"
```

Expected: **2 run, 2 fail** (the default is still 100 ms). If either passes, stop and report.

- [ ] **Step 2: Invert the default and re-base the gate hold**

In **each** file:

1. Replace the **entire** existing `/// <summary>` doc comment above `TestTimeout`, together with the `TestTimeout` line itself. In OurAirports the comment ends `…take 3 real minutes.</summary>`; in Mledoze it ends `…noticeably slow.</summary>`. The replacement:

```csharp
    /// <summary>
    /// The download deadline every test gets unless it passes its own (#433). The source enforces it
    /// on the wall clock, and it covers sending the request AND parsing the response, so it must be
    /// far longer than a slow CI runner can take for either. At 100 ms it timed out ordinary lookups
    /// (#431, #433). Tests that exist to watch the deadline fire pass <see cref="ShortDeadline"/>.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
```

2. **Delete** `GatedDownloadTimeout` and its doc comment entirely.

3. Replace `GateHold` and its doc comment with:

```csharp
    /// <summary>
    /// How long the gated tests deliberately keep the gate closed before releasing it: longer than
    /// <see cref="ShortDeadline"/>, so if the default deadline ever shrinks back to a short value,
    /// the gated tests fail on every run rather than only on a slow one (#431, #433).
    /// </summary>
    private static readonly TimeSpan GateHold = ShortDeadline * 3;
```

4. In the two gated tests, change `Source(handler, downloadTimeout: GatedDownloadTimeout)` back to `Source(handler)`.

5. In `LookupAsync_ReturnsASourceError_WhenNothingAnswers`, change `Source(new NeverRespondingHandler())` to `Source(new NeverRespondingHandler(), downloadTimeout: ShortDeadline)`.

6. In `LookupAsync_ReturnsASourceError_WhenTheBodyStalls`, change `Source(new StalledBodyHandler())` to `Source(new StalledBodyHandler(), downloadTimeout: ShortDeadline)`.

Afterwards, `grep -n "GatedDownloadTimeout"` over both files must print **nothing**.

- [ ] **Step 3: Green, and nothing silently slower**

Run both classes:

```
dotnet test FlightAssistant.Infrastructure.Tests --filter "FullyQualifiedName~Verification.OurAirportsVerificationSourceTests|FullyQualifiedName~Verification.MledozeCountriesVerificationSourceTests" --logger "console;verbosity=normal"
```

Expected: every test passes, and the count equals both classes' test totals.

**Then list every test that took longer than 1 s.** Only the gated tests and the new slow-response tests should take noticeably long, at about 0.3 s each. A test taking seconds means it silently depended on the old short deadline: **stop and report it** rather than adjusting it.

- [ ] **Step 4: Prove the lock by mutation, then show it is stable**

Temporarily set `TestTimeout` back to `TimeSpan.FromMilliseconds(100)` in both files and run the Step 3 filter. Expected: at least the 2 slow-response tests and the 4 gated tests fail. Record the failing names, then restore the file exactly and confirm with `git diff` that no mutation remains.

Then run the Step 3 filter **20 times** with `--no-build` after one build. Every run must be fully green.

- [ ] **Step 5: Full check and commit**

```
dotnet test                                   # full suite; per-project totals as observed
dotnet build --configuration Release          # 0 errors, 0 warnings
git diff --stat origin/development -- FlightAssistant.Infrastructure/ FlightAssistant.API/ FlightAssistant.Application/ FlightAssistant.Domain/ FlightAssistant.Contracts/   # must print NOTHING
```

```bash
git add FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/OurAirportsVerificationSourceTests.cs \
        FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/MledozeCountriesVerificationSourceTests.cs
git commit -m "test: give verification-source tests a long deadline by default, short only where it must fire (#433)"
```
