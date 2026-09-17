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
- **No metered AeroAPI calls, and no live AeroDataBox calls in tests.** Tests drive the provider through a scripted `HttpMessageHandler` against hand-authored fixtures, exactly as `AeroApiFlightSearchProviderTests` does.
- The build stays warning-free; commits use explicit paths; nothing pushed.

## Amendments during execution (2026-09-17)

Several statements in this plan were wrong as first written, and a live measurement after the final review corrected more. They are corrected in place below.

- **The query window.** Step 2 originally said to query the whole local day, `T00:00`–`T23:59`, "as the removed provider did". AeroDataBox **documents** a maximum window of **12 hours**, quoted in issue #267. #267 blamed the removed provider's empty results on that limit. That provider swallowed every error unlogged, though, so the 400 was inferred from the documentation rather than observed, and no call on this branch has measured the limit either. The day is now two sequential ≤12h windows, so an uncached route search makes **up to 2 calls**.
- **The date filter.** Step 2 originally said to filter "using the row's own local time" without saying which time, and Step 4 described excluding a row by its revised time. Rows are kept on their **scheduled** local date instead. That is what `ServiceDate` means everywhere in this system, and it is what `AeroApiFlightStatusProvider` matches status syncs on.
- **The error buckets.** Step 3 originally sent every unclassified status to 502. Any other 4xx now maps to **500**. A 4xx cannot be told apart from a malformed request of ours, such as the 12-hour cap above, and a 500 keeps a systematic defect loud; #267's failure was silent for that provider's whole life. An unknown airport was later measured to return a 204 (no data), not a 4xx. Whether an out-of-range date yields a 4xx is unmeasured; if it does, that surfaces as a 500 too.
- **The fixture.** The constraints and Step 4 called it "recorded". No raw spike payload was kept, so `departures.json` is hand-authored. A real response recorded on 2026-09-17 was later added as `departures-sof-2026-09-18-recorded.json`, and the hand-authored file was brought to its shape.
- **The payload shape (live measurement, 2026-09-17, after the final review).** Four user-approved calls found that `departure.airport` is absent from every departures row (36 of 36). The origin therefore comes from the search criteria, since it is the queried airport. The original parser required that field and would have dropped every real row. "No departures" is a **204**, and so is an unknown airport. A date about 11 months ahead returned far fewer rows (8 against 36 for the next day). Each call costs exactly one request of a hard-capped 1,505 per period, which allows about 752 uncached route searches. Local times carry a UTC offset, not an IANA zone.

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

`AeroDataBox--ApiKey` exists in Key Vault but, when this plan was written, was wired into nothing. Follow the **`GitHubOptions` precedent** (`GitHubOptions.cs:34`), not the `AeroApiOptions` one: the key is **optional**, the app still starts without it, and with no key the selector sends route searches to AeroAPI instead.

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

        public const string ApiBaseUrl = $"https://{RapidApiHost}/";   // amended: a constant, not configurable

        public string? ApiKey { get; set; }
        [Range(1, 300)] public int TimeoutSeconds { get; set; } = 30;   // amended: validated on start

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
    }
}
```

- [ ] **Step 2: The provider**

`GET /flights/airports/iata/{iata}/{fromLocal}/{toLocal}` with
`withLeg=true&direction=Departure&withCancelled=false&withCargo=false&withPrivate=false&withCodeshared={IncludeCodeshares}`.

Three things from the spike that the implementation must honour (the 12-hour limit in fact 2 is documented, not spike-measured):

1. **`withCodeshared` maps directly onto `IncludeCodeshares`** — `true` adds `IsCodeshared` rows alongside the `IsOperator` ones (FRA, one hour: 42 rows → 158). Pass the flag through; do not filter client-side.
2. **The window filters on *revised* time, not scheduled.** A flight scheduled 09:35 but revised to 10:01 is **absent** from a 09:00–10:00 window. So query the whole local day, then keep each row on its own **scheduled** local date. The endpoint documents a 12-hour maximum window (quoted in #267; not measured), so the day is two sequential windows, `T00:00`–`T11:59` and `T12:00`–`T23:59`, merged and deduped by flight number plus scheduled UTC. *(Amended; see Amendments above.)*
3. **Rows carry both UTC and local times, with the local time's UTC offset inline**, so this provider needs **no timezone lookup** — none of `AeroApiAirportTimezoneLookup`'s machinery, and no `/airports/{id}` call.

Filter each row to the requested `ArrivalIata` client-side (AeroDataBox has no destination filter) and map to `FlightSearchResult`. Read the response's `departures` array; return an empty list when it is missing or not an array.

- [ ] **Step 3: Failure handling**

Follow `AeroApiResponse.ReadOrThrowAsync`'s *shape* — a `DomainException` whose status distinguishes our problem from theirs — but **do not reuse the AeroAPI type**; it names AeroAPI in its messages. 401/403 → 500 (our key or plan), 429 → 503 with `Retry-After`, any other 4xx → 500 (indistinguishable from a malformed request of ours; see Amendments), and everything else, including 5xx, unreachable, timeout and unparseable responses, → 502. A 204, or a successful response with no matching rows, is an **empty list**, not an error. A 200 with an empty body is unparseable, so it is a 502. *(Amended; see Amendments above.)*

- [ ] **Step 4: Tests**

Mirror `AeroApiFlightSearchProviderTests`: a scripted `HttpMessageHandler`, a hand-authored fixture, and assertions on the **request URL** as well as the mapped results. Write `departures.json` by hand in the shape the spike returned (number, `codeshareStatus`, departure/arrival with `scheduledTime` `utc`+`local`, airport `iata`, aircraft). Cover at minimum:

- `IncludeCodeshares: false` → `withCodeshared=false` in the URL; `true` → `withCodeshared=true`.
- Rows for other destinations are filtered out.
- Rows are kept or excluded on their **scheduled** local date. One merely *delayed* within the day is kept; one scheduled the previous day but revised into this one is excluded. Every issued window is ≤ 12 hours. *(Amended; see Amendments above.)*
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
if (options.IsConfigured)   // amended: never add the key header with a null value
    client.DefaultRequestHeaders.Add("X-RapidAPI-Key", options.ApiKey);
client.DefaultRequestHeaders.Add("X-RapidAPI-Host", AeroDataBoxOptions.RapidApiHost);
```

**No throttle gate and no resilience pipeline** on this client — `AeroApiThrottleGate` exists to protect a metered tier's rate limit and is AeroAPI-specific. Say so in a comment. *(Amended: automatic redirects are also disabled on this client, so the key header is never replayed to another host, and its options are validated on start.)*

**Two consequences to state in comments, not discover later:**

- **The cache key has no provider dimension** (`dep|arr|date|fn|cs`). Routing depends on criteria shape **and** on whether the AeroDataBox key is configured. So after the key is added or removed, a cached route result from the other provider can be served until its TTL expires, and a rollback should flush the search-cache prefix. If anyone later routes the same shape to both providers at once, the key must gain a provider dimension. *(Amended: this originally said routing depended on shape alone.)*
- **A route search is no longer refused when the AeroAPI budget is exhausted**, because it no longer touches AeroAPI. That is an improvement, and it is a behaviour change — `AeroApiFlightSearchBudgetDecoratorTests` may assert the old behaviour; if a test there covers a route search, **stop and report** rather than editing it. *(Amended: its tests do use route searches, but they construct the decorator directly rather than through the selector or DI, so routing cannot reach them. The chain-level change is pinned by new DI tests instead.)*

- [ ] **Step 7: Document it**

Add a CLAUDE.md paragraph covering: the split and why flight-number search stays on AeroAPI; that AeroDataBox needs no timezone lookup; the two ≤12h windows and the scheduled-date keeper (the server's window filters on revised time; rows are kept on scheduled local date); the error buckets; that the key is optional and its absence falls back to AeroAPI; and that the budget decorator deliberately wraps only the AeroAPI path.

State only what this change makes true. **Do not write "every search…" or "all searches…"** — that class of sentence has repeatedly been wrong in this repository's recent history.

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
