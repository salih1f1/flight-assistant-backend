# Spec — admin provider routing switch, and a map of which provider is called when (#531)

Issue: #531 · Date: 2026-09-19 · Status: approved in discussion, not started

## Context

Provider routing is spread across configuration keys and code, and none of it is either visible or
changeable without a deploy:

- **Route search** picks AeroDataBox over AeroAPI inside `FlightSearchProviderSelector`, gated on
  `AeroDataBox:ApiKey` being present (#422). Changing that decision means editing configuration and
  restarting.
- **`Verification:DefaultSources:Airport|Airline|Country`** is configuration-only and read at
  startup. The `Airline` default is `aeroapi`, which bills **$0.015 per lookup**, against free
  `ourairports` and `mledoze` for the other two entity types. That is the single most steerable
  cost in the system and it currently takes a restart to steer.
- **Nothing states which provider is called on which operation.** Answering "when I add a flight,
  which providers run?" today means reading `TrackFlightCommandHandler`, `ReferenceDataProvisioner`
  and `FlightTrackedVerificationEventHandler`.

The purpose of this work is **cost steering**: move a job to the cheaper provider as budgets change,
with the numbers needed to make that call sitting beside the switch. It is deliberately *not* an
experimentation framework and not a kill switch for outages, though it can serve as one.

## Goals

1. Four routing decisions become changeable at runtime, persisted, audited, and reversible without a
   deploy.
2. One endpoint answers "which provider is called when", including for the decisions that are *not*
   switchable, each with the reason it is fixed.
3. Every switchable row carries the two numbers a steering decision needs: the **list price** of each
   option, and the **live headroom** remaining on each provider.

## Non-goals

- No second flight-status provider. Status stays on AeroAPI: the deleted `AeroDataBoxFlightStatusProvider`
  (`21e55cf^`) read `GET /flights/number/{n}/{date}`, which was measured to carry **no gate and no
  baggage claim**, so adopting it would lose fields `FlightStatusEvent.CreateDetailChange` records
  (#351). Revisit under #364, not here.
- No switch for flight-number search. #422 established that AeroDataBox resolves a marketing number
  to its operator (`6E4120` → `TK 1028`), so routing it there could change **which flight a traveler
  tracks**. That is a correctness hazard, not a cost trade-off.
- No local call metering or per-job spend attribution. PR #294 was closed for exactly this:
  FlightAware bills per page and bills failed calls, and exposes no per-request identifier, so
  anything counted and priced locally is wrong in both directions and can never be reconciled
  against the invoice. `GET /admin/usage/flight-data` (#503) already carries the honest version of
  this, with its calculated figures labelled as calculated.

## Scope: what is switchable, and what is not

Four keys, in a closed `ProviderJobs` list:

| Key | Options | Default source of truth |
|---|---|---|
| `flight-search-route` | `aerodatabox`, `aeroapi` | `AeroDataBox:ApiKey` presence |
| `verification-airport` | any registered source supporting `Airport` | `Verification:DefaultSources:Airport` |
| `verification-airline` | any registered source supporting `Airline` | `Verification:DefaultSources:Airline` |
| `verification-country` | any registered source supporting `Country` | `Verification:DefaultSources:Country` |

Everything else appears on the map read-only, carrying the reason it is fixed.

---

## 1. The switch mechanism

This mirrors the `JobSwitch` mechanism (#471) closely and deliberately: that pattern already solved
persistence across deploys, the configuration ceiling, read caching on a hot path, and audit with an
operator reason.

### Domain and persistence

New entity `ProviderRoute` (`FlightAssistant.Domain/Providers/Entities/`), table `provider_routes`:

```
Job         string    PK  -- a ProviderJobs value
Providers   string        -- comma-joined, same grammar as VerificationDefaultSources
ChangedUtc  DateTime?
ChangedBy   string?
Reason      string?       -- operator note, rendered beside the state
```

**A sibling table rather than a widening of `job_switches`.** `JobSwitch.Enabled` is a boolean and
`JobNames` states its closed list is what keeps it "two booleans with a config ceiling, not a
feature-flag framework". Adding a nullable string column to that table to carry a *value* would
weaken both. A sibling with the same shape keeps each registry closed on its own terms.

**`Providers` is a list, not a single name.** `VerificationDefaultSources.For` splits on
`VerificationRequest.SourceSeparator` and a request may name several sources in the order they are
asked (`ourairports,aeroapi`). The switch must preserve that, so it stores and validates the same
comma-joined grammar via `VerificationRequest.TryJoinSources`. `flight-search-route` happens to hold
a single name; nothing in the storage shape assumes it.

### Read path

`IProviderRouting` (Application) + `CachedProviderRouting` (Infrastructure), following
`CachedJobSwitch` including its test seam:

- `GetProvidersAsync(job, ct)` — cached per key for a 10 s window. Both the search path and the
  verification trigger read it per request, so an uncached read would be a database round trip per
  search. Named distinctly from `IFlightSearchRouter.ResolveAsync` in §2, which answers a different
  question: this one reads a stored key, that one applies the criteria-shape rule on top of it.
- `GetAsync` — uncached, for the admin page reading its own state back after a write.
- The real reader resolves a scoped `FlightAssistantDbContext` from `IServiceScopeFactory` per call,
  because the context is scoped and this service is a singleton.

**One deliberate difference from `CachedJobSwitch`: no fail-open/fail-closed split.** That class
needs one because a boolean read failure has two plausible wrong answers, and guessing "enabled"
can spend real money. A routing read failure has an unambiguous right answer: **fall back to the
configured default**, which is always available in-process. Logged at Warning, never guessed, and
cached like a real answer so the retry happens when the entry ages out rather than on a separate
schedule.

### Configuration is a hard ceiling

Exactly as `ConfigEnabled` / `CanToggle` already work for jobs. With `AeroDataBox:ApiKey` unset,
`flight-search-route` reports `CanToggle: false` and routes to AeroAPI whatever the row says — a
switch must never be able to select a provider the deployment cannot authenticate to. `PUT` against
a ceilinged key is a **409**, matching `ToggleJobCommand`.

### Write path and audit

`PUT /admin/providers/{job}` taking `{ providers, reason }`, mirroring `PUT /admin/jobs/{job}`:
200 with the updated row, 404 for an unknown key, 409 for a ceiling, 400 for an unusable provider.

- `ProviderRoute` joins `AdminAuditInterceptor.NotAuditedTypes`, and the command handler writes its
  audit row **explicitly**. This is the reasoning that comment already records for `JobSwitch`: the
  write is rare and deliberate, and the operator's `reason` text has to be carried, which the
  interceptor cannot do. Letting both fire would put two rows in the log for one change, saying
  different things.
- Registered in `BrowsableTables` as `provider-routes` with every mapped column exposed and nothing
  masked (a provider name and an admin id, like `VerifiedBy`). Omitting this fails
  `BrowsableTablesModelCompletenessTests`, which is the intended behaviour of that guard.

### Validation the configuration path cannot do

The write validates each named source against `IVerificationSourceRegistry` — that it is registered
**and** that its `SupportedEntityTypes` includes the key's entity type — and rejects it with a 400
naming the source. `VerificationDefaultSources.Validate` checks only the join grammar, which is why
CLAUDE.md records that the configured `aeroapi` default would have completed every request straight
to `Error` before #401 registered it. A switch write is a request/response, so it can refuse.

---

## 2. Where each switch is read

### Route search

The routing decision moves out of `FlightSearchProviderSelector` into `IFlightSearchRouter`, one
method: `ResolveAsync(criteria, ct)` → provider name. Two readers, one rule:

1. **`FlightSearchProviderSelector`** uses it to pick the provider. The criteria-shape logic is
   unchanged — the switch only redirects a search that is *already* route-shaped, so every shape
   AeroAPI's own search guard rejects still reaches that guard and produces the same error.
2. **`AeroApiFlightSearchCachingDecorator`** uses it to build the cache key.

That second reader is the fix for a problem already recorded in `DependencyInjection.cs`: the cache
key `dep|arr|date|fn|cs` has **no provider dimension**, so a route-search key can be served the
other provider's rows for up to `SearchCacheTtlFuture` (12 h) after routing changes. Today that
requires a deploy and a runbook step ("flush the `aeroapi:flight-search:` prefix"). A runtime switch
would make it a per-button-press problem. `BuildCacheKey` therefore gains the resolved provider,
which retires the runbook step permanently.

The decorator must **not** read the switch itself — that would be a second copy of the routing rule,
free to drift. `FlightSearchProviderSelector` already avoids exactly this by *calling*
`AeroApiFlightSearchProvider.HasUsableFlightNumber` rather than restating its `>= 2` rule.

`ICacheService` offers only `Get`/`Set`/`Remove`, so flushing a prefix was the alternative and is
rejected: it would need a new capability that the `IMemoryCache` fallback cannot implement at all
without tracking every key it has written.

### Verification defaults

`IVerificationJobOptions.DefaultSourcesFor` has exactly two callers:

- `FlightTrackedVerificationEventHandler` (the #410 flight-tracking trigger), and
- `ReferenceDataProvisioner` (the #465 track-time provisioning path).

Both are already async. A new `IVerificationSourceDefaults.ForAsync(entityType, ct)` reads the switch
and falls back to `DefaultSourcesFor`, which stays in place as the fallback rather than being
deleted. Both call sites change to it.

**The verification job itself is untouched.** It uses each request's own stored `sources`, per
CLAUDE.md. So flipping `verification-airline` affects newly **queued** requests and the track-time
provisioning path, and leaves every request already in the queue alone. This must be stated in the
endpoint description: otherwise "I flipped the switch and the next cycle still called the old
source" reads as a defect rather than as the design.

---

## 3. The map

`GET /admin/providers` returns rows grouped by **what triggers the call**.

| Trigger | Provider(s), in call order | Switchable |
|---|---|---|
| `POST /flights` — track | none directly; a verification source **only** when the airline/airport/country row is missing (#465) | via the three verification rows |
| `GET /flights/search` — route shape | AeroDataBox, else AeroAPI when unconfigured | **yes** |
| `GET /flights/search` — flight-number shape | AeroAPI `/schedules` | no — #422, `6E4120` → `TK 1028` |
| ↳ AeroAPI search only | AeroAPI `/airports/{id}`, once per distinct origin, cached 365 d | no — follows the search provider |
| sync — Fast/Live mode | AeroAPI `/flights/{ident}` | no — only implementation; the alternative lacks gate and baggage claim |
| sync — Schedule mode (#444) | AeroAPI `/schedules`, via a flight-number search | no — follows flight-number search |
| verification — Airport | `ourairports`, free | **yes** |
| verification — Airline | `aeroapi`, **$0.015/lookup** | **yes** |
| verification — Country | `mledoze`, free | **yes** |
| budget guard + usage gauge | AeroAPI `/account/usage`, $0.00 | no |

Each row carries: the trigger, the providers in call order, `switchable`, `fixedReason` (null when
switchable), `canToggle`, the selectable options, list price per option, and — for a switchable row —
`changedUtc` / `changedBy` / `reason`.

**An authored registry, not a scan**, following `DataSourceCatalog`'s own precedent: the trigger
prose and the fixed-reasons cannot be derived from the code, and a completeness guard is what keeps
an authored list honest (see §5).

---

## 4. Headroom numbers

Two figures per provider, and the distinction between them matters.

**List price** is authored prose with its own date: `"$0.020/page, list as of 2026-09-19"`. It cannot
be derived, and it will drift when FlightAware or RapidAPI reprices. Stamping the date makes that
staleness visible rather than silent — the same posture as `dataAsOfUtc` being null on Azure cost
rather than implying the figure is current.

**Live headroom** comes from the provider, never a local meter:

- **AeroAPI**: `IProviderBudgetGuard.GetStateAsync` plus the spend figure already cached in
  `AeroApiUsageCache`. Both exist, share one free upstream read per 10 minutes with the usage gauge
  and the budget guard, and so cost no extra metered call and no extra throttle slot.
- **AeroDataBox**: new, and small. A `DelegatingHandler` on the AeroDataBox named client reads
  `x-ratelimit-requests-limit` and `x-ratelimit-requests-remaining` off responses into a singleton
  holder — the same shape as `DataSourceObservingHandler`, and zero extra calls, since it reads
  headers off traffic already being sent. These headers are currently captured nowhere; #422
  measured the 1,505 figure by hand.

`AeroDataBoxQuota` is **null until this process has made a call**, and is reported as *unknown*,
never `0`. This follows the rule `AdminHealthMigrationsDto.Available` already encodes: "cannot tell"
is a different and much weaker claim than "nothing left", and conflating them here would show an
operator an exhausted quota on every fresh deploy.

---

## 5. Guards and tests

**Map completeness.** An authored map rots, so a guard test asserts every concrete provider in the
Infrastructure assembly appears in at least one map row. The obligation rule must be structural
rather than incidental: a type implementing `IFlightSearchProvider`, `IFlightStatusProvider` or
`IVerificationSource` that does **not** take one of those interfaces as a constructor parameter is a
real provider, because decorators and selectors wrap one and providers do not. Every real provider
owes a row.

This test must be **mutation-tested both ways before it is trusted**: delete a map row and confirm
it goes red; add a throwaway provider and confirm it goes red. A sweep-style guard that passes in
both directions is worse than no guard, because it reads as coverage.

**Cache key.** A test proves the same criteria produce different keys under the two providers, and
that a flip does not serve the previous provider's cached rows. This is the defect the provider
dimension exists to prevent, and it is invisible without a test that flips mid-TTL.

**Switch semantics.** Following `CachedJobSwitchTests`, against the in-memory reader seam: the cache
window bounds how long a flip takes to apply; a read failure falls back to the configured default
rather than guessing; the ceiling cannot be overridden.

**PostgreSQL-backed.** The audit row for a switch write belongs in the Infrastructure suite, not the
API suite: `FlightAssistantWebApplicationFactory` strips the audit interceptor entirely (#319), so an
API-level assertion would pass whether or not the row was written.

---

## 6. Consequences and accepted costs

1. **Forcing route search to AeroAPI can produce 503s.** `AeroApiFlightSearchBudgetDecorator`
   refuses from `SoftLimit` (80% of budget) onward, and that decorator sits on the AeroAPI branch
   the switch would select. So the flip can start refusing route searches at exactly the moment a
   cost-conscious operator reaches for it. Not a defect and not worked around — surfaced as
   `fixedReason`-style prose on that row so it is read before the button is pressed.
2. **A flip costs one extra cache miss per key per provider**, bounded by the TTL. Accepted: the
   alternative is the stale-rows window the provider dimension removes.
3. **Verification flips do not reach queued requests.** Stated in §2 and in the endpoint description.
4. **List price can go stale.** Mitigated by the stamped date, not by automation: neither provider
   exposes its own price list through an API.
5. **A per-instance quota reading.** The AeroDataBox headroom holder is per process, so a scaled-out
   deployment shows whichever instance served the request. The same caveat every rate limiter in
   this repository already carries.

---

## 7. Delivery

Three backend PRs, each its own issue and branch off `development`, plus one admin-panel PR in the
separate panel repository.

1. **Switch mechanism + route search + cache key.** `ProviderRoute`, migration,
   `IProviderRouting`/`CachedProviderRouting`, `IFlightSearchRouter`, the cache-key dimension,
   `GET`/`PUT /admin/providers` for the one key, audit, `BrowsableTables`.
2. **Verification defaults.** `IVerificationSourceDefaults`, both call sites, registry validation on
   write, the three remaining keys.
3. **Map + headroom.** The authored registry and its completeness guard, list prices, the AeroAPI
   headroom read, and the AeroDataBox quota handler.

Each PR regenerates `spec/openapi.json` via `scripts/generate-openapi.sh`, or CI fails the build.
