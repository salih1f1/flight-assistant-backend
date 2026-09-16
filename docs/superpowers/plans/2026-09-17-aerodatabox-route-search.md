# AeroDataBox Route Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve route searches (origin + destination) from AeroDataBox's free tier instead of AeroAPI, cutting the cost of the common search path to zero without changing the response contract.

**Architecture:** A new `AeroDataBoxFlightSearchProvider` plus a selector that routes by criteria shape. Flight-number searches stay on AeroAPI unchanged. The selector sits **inside** the existing cache and **outside** the AeroAPI budget decorator.

**Tech Stack:** .NET 10, `IHttpClientFactory`, xUnit, NSubstitute.

**Spec:** issue #422, and the codeshare spike recorded on it.

## Global Constraints

- **The response contract does not change.** `FlightSearchResult`, `FlightSearchResultDto` and the `GET /flights/search` shape stay exactly as they are. `spec/openapi.json` must show **no diff** — if regenerating produces one, stop and report.
- **Flight-number search stays on AeroAPI.** AeroDataBox resolves a marketing number to its operator (`6E4120` → `TK 1028`), which would change what a traveler sees. Decided on #422; do not "simplify" by routing everything to one provider.
- **The selector must mirror the existing guard exactly**, not approximately: a flight number counts only when `!string.IsNullOrWhiteSpace(fn) && fn.Trim().Length >= 2` (`AeroApiFlightSearchProvider.HasUsableFlightNumber`). Reuse that rule; do not restate it with different wording.
- **Never wrap AeroDataBox in `AeroApiFlightSearchBudgetDecorator`.** It throws 503 based on *AeroAPI* spend. Gating a free provider on it would refuse free searches for no reason.
- **No metered AeroAPI calls, and no live AeroDataBox calls in tests.** Tests drive the provider through a scripted `HttpMessageHandler` against recorded fixtures, exactly as `AeroApiFlightSearchProviderTests` does.
- The build stays warning-free; commits use explicit paths; nothing pushed.

---

## File Structure

- **Create** `FlightAssistant.Infrastructure/ExternalServices/AeroDataBox/AeroDataBoxOptions.cs`
- **Create** `FlightAssistant.Infrastructure/ExternalServices/AeroDataBox/AeroDataBoxFlightSearchProvider.cs`
- **Create** `FlightAssistant.Infrastructure/ExternalServices/FlightSearchProviderSelector.cs`
- **Create** `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroDataBox/AeroDataBoxFlightSearchProviderTests.cs`
- **Create** `FlightAssistant.Infrastructure.Tests/ExternalServices/FlightSearchProviderSelectorTests.cs`
- **Create** `FlightAssistant.Infrastructure.Tests/Fixtures/AeroDataBox/departures.json`
- **Modify** `FlightAssistant.Infrastructure/DependencyInjection.cs`
- **Modify** `CLAUDE.md`

---

### Task 1: The provider and its options

**Files:** `AeroDataBoxOptions.cs`, `AeroDataBoxFlightSearchProvider.cs`, `AeroDataBoxFlightSearchProviderTests.cs`, `departures.json`.

- [ ] **Step 1: Options, optional by design**

`AeroDataBox--ApiKey` exists in Key Vault but is wired into nothing today. Follow the **`GitHubOptions` precedent** (`GitHubOptions.cs:34`), not the `AeroApiOptions` one: the key is **optional**, the app still starts without it, and an unconfigured provider degrades rather than crashing.

```csharp
namespace FlightAssistant.Infrastructure.ExternalServices.AeroDataBox
{
    /// <summary>
    /// AeroDataBox via the RapidAPI marketplace (#422). The key is optional — like GitHub:Token,
    /// its absence must not stop the app starting; route search falls back to AeroAPI instead.
    /// </summary>
    public sealed class AeroDataBoxOptions
    {
        public const string RapidApiHost = "aerodatabox.p.rapidapi.com";

        public string? ApiKey { get; set; }
        public string BaseUrl { get; set; } = $"https://{RapidApiHost}/";
        public int TimeoutSeconds { get; set; } = 30;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
    }
}
```

- [ ] **Step 2: The provider**

`GET /flights/airports/iata/{iata}/{fromLocal}/{toLocal}` with
`withLeg=true&direction=Departure&withCancelled=false&withCargo=false&withPrivate=false&withCodeshared={IncludeCodeshares}`.

Three things measured during the spike that the implementation must honour:

1. **`withCodeshared` maps directly onto `IncludeCodeshares`** — `true` adds `IsCodeshared` rows alongside the `IsOperator` ones (FRA, one hour: 42 rows → 158). Pass the flag through; do not filter client-side.
2. **The window filters on *revised* time, not scheduled.** A flight scheduled 09:35 but revised to 10:01 is **absent** from a 09:00–10:00 window. So query a **widened** window and filter to the requested service date afterwards, using the row's own local time. Use the whole local day (`T00:00`–`T23:59`) as the removed provider did, then filter.
3. **Rows carry both UTC and local times with the IANA zone inline**, so this provider needs **no timezone lookup** — none of `AeroApiAirportTimezoneLookup`'s machinery, and no `/airports/{id}` call.

Filter each row to the requested `ArrivalIata` client-side (AeroDataBox has no destination filter) and map to `FlightSearchResult`. Read the response's `departures` array; return an empty list when it is missing or not an array.

- [ ] **Step 3: Failure handling**

Follow `AeroApiResponse.ReadOrThrowAsync`'s *shape* — a `DomainException` whose status distinguishes our problem from theirs — but **do not reuse the AeroAPI type**; it names AeroAPI in its messages. 401/403 → 500 (our key or plan), 429 → 503 with `Retry-After`, anything else/unreachable/unparseable → 502. A successful response with no matching rows is an **empty list**, not an error.

- [ ] **Step 4: Tests**

Mirror `AeroApiFlightSearchProviderTests`: a scripted `HttpMessageHandler`, a recorded fixture, and assertions on the **request URL** as well as the mapped results. Record `departures.json` from the shape the spike returned (number, `codeshareStatus`, departure/arrival with `scheduledTime` `utc`+`local`, airport `iata`, aircraft). Cover at minimum:

- `IncludeCodeshares: false` → `withCodeshared=false` in the URL; `true` → `withCodeshared=true`.
- Rows for other destinations are filtered out.
- A row whose revised time falls outside the requested day is excluded, and one merely *delayed* within it is **kept** — the gotcha from Step 2.
- 401 → 500, 429 → 503, malformed body → 502, empty `departures` → empty list.

---

### Task 2: The selector and wiring

**Files:** `FlightSearchProviderSelector.cs`, `FlightSearchProviderSelectorTests.cs`, `DependencyInjection.cs`, `CLAUDE.md`.

- [ ] **Step 5: The selector**

```csharp
/// <summary>
/// Routes a search by criteria shape (#422): a full route goes to AeroDataBox, whose free tier
/// makes it cost nothing; anything anchored on a flight number stays on AeroAPI, because
/// AeroDataBox resolves a marketing number to its operator (6E4120 -> TK 1028) and would change
/// which flight a traveler tracks. Falls back to AeroAPI when AeroDataBox is unconfigured.
/// </summary>
```

Route to AeroDataBox **only** when there is no usable flight number *and* both airports are present. Every other shape — including the invalid ones the guard rejects — goes to AeroAPI, so its existing guard keeps producing the same error for the same input.

- [ ] **Step 6: Wiring**

In `DependencyInjection.cs`, the chain becomes:

```
AeroApiFlightSearchCachingDecorator(
    FlightSearchProviderSelector(
        aeroDataBox: AeroDataBoxFlightSearchProvider,
        aeroApi:     AeroApiFlightSearchBudgetDecorator(AeroApiFlightSearchProvider, ...)),
    ...)
```

Register the named client following the `"AeroApi"` pattern, with RapidAPI's two headers:

```csharp
client.DefaultRequestHeaders.Add("X-RapidAPI-Key", options.ApiKey);
client.DefaultRequestHeaders.Add("X-RapidAPI-Host", AeroDataBoxOptions.RapidApiHost);
```

**No throttle gate and no resilience pipeline** on this client — `AeroApiThrottleGate` exists to protect a metered tier's rate limit and is AeroAPI-specific. Say so in a comment.

**Two consequences to state in comments, not discover later:**

- **The cache key has no provider dimension** (`dep|arr|date|fn|cs`). That is safe *only because* the split is deterministic by criteria shape, so a given key always reaches the same provider. If anyone later routes the same shape to both, the key must gain one.
- **A route search is no longer refused when the AeroAPI budget is exhausted**, because it no longer touches AeroAPI. That is an improvement, and it is a behaviour change — `AeroApiFlightSearchBudgetDecoratorTests` may assert the old behaviour; if a test there covers a route search, **stop and report** rather than editing it.

- [ ] **Step 7: Document it**

Add a CLAUDE.md paragraph covering: the split and why flight-number search stays on AeroAPI; that AeroDataBox needs no timezone lookup; the revised-time window gotcha; that the key is optional and its absence falls back to AeroAPI; and that the budget decorator deliberately wraps only the AeroAPI path.

State only what this change makes true. **Do not write "every search…" or "all searches…"** — that class of sentence has been wrong four times in this repository's recent history.

- [ ] **Step 8: Full check and commit**

```
dotnet test    # report the totals you observe
dotnet build --configuration Release    # 0 errors, 0 warnings
scripts/generate-openapi.sh
git diff --stat spec/openapi.json    # must print NOTHING
```

```bash
git add FlightAssistant.Infrastructure/ExternalServices/AeroDataBox/ \
        FlightAssistant.Infrastructure/ExternalServices/FlightSearchProviderSelector.cs \
        FlightAssistant.Infrastructure/DependencyInjection.cs \
        FlightAssistant.Infrastructure.Tests/ExternalServices/AeroDataBox/ \
        FlightAssistant.Infrastructure.Tests/ExternalServices/FlightSearchProviderSelectorTests.cs \
        FlightAssistant.Infrastructure.Tests/Fixtures/AeroDataBox/ \
        CLAUDE.md
git commit -m "feat: serve route searches from AeroDataBox's free tier (#422)"
```
