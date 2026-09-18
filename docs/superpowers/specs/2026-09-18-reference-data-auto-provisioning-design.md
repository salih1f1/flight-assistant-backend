# Reference-data auto-provisioning on track

**Issue:** #465
**Date:** 2026-09-18
**Status:** Design approved, pending implementation plan

## Problem

`GET /flights/search` never consults our database. `SearchFlightsQueryHandler` hands the
criteria straight to `IFlightSearchProvider`, so a flight through an airport or on an airline
we have never imported comes back in the picker normally.

`POST /flights` then rejects it. `TrackFlightCommandHandler` resolves three ids before doing
anything else, and each miss is a 404:

| Missing row | Today's response |
|---|---|
| Airline (first two characters of the flight number) | `404 Airline 'XX' not found.` |
| Departure airport | `404 Airport 'XXX' not found.` |
| Arrival airport | `404 Airport 'XXX' not found.` |

So the picker offers a flight that cannot be tracked. This is the same class of defect
CLAUDE.md already describes for flight-number format drift between the search and track
validators, except for reference data it is still open. It bites hardest on small and
regional airports, and on airlines whose row carries only an ICAO code — `Airline.Iata` is
nullable and the lookup matches on IATA alone, with no ICAO fallback.

Countries are not checked on either path. A country only matters to admin writes, to CSV
import, and to the verification accept-source flow.

## Goal

When a reference row is missing, look the entity up in the external sources we already use
for verification. If it exists, create the row, stamp it verified, and let the track succeed.
Return 404 only when the source is loaded and genuinely has no such entity.

## Approach

A new Application service, `ReferenceDataProvisioner`, reusing `IVerificationSourceRegistry`.

Two alternatives were rejected:

- **Reuse the verification queue synchronously.** The job polls every 30 seconds and its
  processor only verifies rows that already exist — `verification_requests.entity_key` is a
  row id for airports and airlines, and a row that does not exist yet has no id. A traveler's
  request would also block on a cycle it cannot influence, which the queue was never designed
  to support.
- **Provision from the flight providers.** Search rows carry only IATA codes, so the created
  rows would be stubs with no name, city or country. It also does nothing for the airline, and
  puts writes on a cached, anonymous read path.

## What the sources can actually supply

`IVerificationSource.LookupAsync` returns exactly the columns
`VerificationCheckedColumns.GetColumns` registers for the entity type:

| Entity | Source | Columns returned | Enough to build a row? |
|---|---|---|---|
| Airport | `ourairports` (free) | `Iata, Icao, Name, City, CountryCode, Latitude, Longitude, ElevationFt` | Yes |
| Country | `mledoze` (free) | `Code, Name, Alpha3, NumericCode, Region, Subregion` | Yes |
| Airline | `aeroapi` ($0.015/lookup) | `Iata, Icao, Name` | **No** |

`Airline.Create` requires a non-null `CountryCode` that is a foreign key into `countries`.
AeroAPI's operator payload reports country as a *name* (`"country": "United Kingdom"`), never
a code — which is exactly why #401 R1 removed `CountryCode` from the airline checked-column
list.

**Resolution.** `AeroApiVerificationSource` additionally places the raw `country` name in its
`Found` values under a key that is not a registered checked column. `VerificationComparer`
only ever reads registered columns and silently ignores extras — its own documented
behaviour — so verification outcomes cannot change. The provisioner resolves that name to a
two-letter code through the `mledoze` dataset (`name.common` → `cca2`).

A name that does not resolve means no valid airline row can be built, so that case is a 404
naming the airline.

*Accepted risk:* this makes a value load-bearing that the comparer deliberately ignores. A
future change to the airline checked-column list must not assume unregistered keys are unused.

## Component

`FlightAssistant.Application/ReferenceData/`:

```csharp
public sealed record MissingReference(VerificationEntityType Type, string Code);

public enum ReferenceProvisionStatus { Provisioned, NotFound, Unavailable }

public sealed record ReferenceProvisionResult(
    ReferenceProvisionStatus Status,
    VerificationEntityType? FailedType,
    string? FailedCode,
    string? DiagnosticDetail);

public interface IReferenceDataProvisioner
{
    Task<ReferenceProvisionResult> ProvisionAsync(
        IReadOnlyCollection<MissingReference> missing, CancellationToken ct);
}
```

One entry point rather than one method per entity type, so the whole step shares a single
wall-clock deadline.

## Handler flow

`TrackFlightCommandHandler`:

1. Resolve the three ids as today. All present → today's path, unchanged, no new calls.
2. Any missing → **check the tracking cap first**, so a refused track still writes nothing.
   This is sound: if any of the three reference rows is absent, no `Flight` row can reference
   it, so the traveler cannot already be tracking this flight and `AlreadyTracksAsync` is
   necessarily false. Only the count check is needed.
3. Call `ProvisionAsync` with the missing set, then re-resolve the ids.
4. Map the outcome:
   - `Provisioned` → continue into the existing path.
   - `NotFound` → `DomainException(<today's message>, 404)`.
   - `Unavailable` → `DomainException(..., 503)`, which `GlobalExceptionHandler` already
     answers with `Retry-After: 60`.

Both `POST /flights` and the admin track route send `TrackFlightCommand`, so both gain this.

## Provisioner internals

In foreign-key order:

1. Fetch each missing airport and the airline from the source named in
   `Verification:DefaultSources:*`; the first source that answers `Found` wins.
2. Collect the country codes those answers imply — an airport's `CountryCode` directly, the
   airline's by resolving AeroAPI's country name through `mledoze`.
3. Insert missing countries, then airports, then the airline.
4. Stamp each row `Verify(VerificationJobActor.ActorId, sourceName)` — actor `system`, source
   `ourairports`/`aeroapi`/`mledoze`, exactly as the job does — and set
   `SourceType = "auto"`, distinguishing these rows from `seed`, `admin` and `import`.
   Note that `Airport.Create` and `Airline.Create` hardcode `SourceType = "admin"`, so the
   provisioner must overwrite it after construction rather than relying on the factory.
5. Save once.

### Own DI scope, carrying the system actor

The provisioner saves through `VerificationJobActor.CreateScope`, not the traveler's request
scope. `AdminAuditInterceptor` audits every tracked change in a `SaveChanges` that carries an
actor, so setting an actor on the traveler's scope would begin auditing ordinary traveler
flight writes — which CLAUDE.md states must stay untouched. A separate scope gets the
reference writes audited under `system` and leaves the traveler's own write unaudited.

### Guards, in front of metered calls only

- **Negative cache.** `ICacheService`, key `refdata:missing:{type}:{code}`, TTL
  `ReferenceProvisioning:NegativeCacheTtl` (default 24 h). Read before any lookup, written on
  an upstream `NotFound`, so a repeated bogus designator costs nothing after the first.
- **Budget guard.** `IProviderBudgetGuard` refuses metered lookups from `SoftLimit` onward,
  matching `AeroApiFlightSearchBudgetDecorator` — the closest analogue, also traveler-facing
  and also discretionary spend — rather than #401's `Exhausted`-only rule, which exists for
  work an admin explicitly asked for. The result is 503, never 404: at the cap we do not know
  whether the airline exists.

The free dataset sources are never gated; they cost nothing.

### Why no background queue is needed

`VerificationDatasetCache` already shares one download across concurrent callers, and a
caller's own cancellation cancels only its own *wait*, never the shared download. On a
deadline timeout we simply return 503 — the OurAirports or mledoze download keeps running,
and a retry seconds later finds it warm. An abandoned `AeroApiThrottleGate` reservation is
likewise released, so a retry just re-queues.

## Configuration

New `ReferenceProvisioning` section, options pattern with `ValidateOnStart` like the rest:

| Key | Default | Range | Notes |
|---|---|---|---|
| `ReferenceProvisioning:Enabled` | `true` | — | `false` restores today's 404 exactly |
| `ReferenceProvisioning:Budget` | `00:00:10` | 1 s–60 s | One wall-clock budget for the whole step |
| `ReferenceProvisioning:NegativeCacheTtl` | `24:00:00` | 0–7 days | `0` disables the cache, so every attempt re-queries the source |

The sources themselves are not configured again here: provisioning reads
`Verification:DefaultSources:*`, so there is one registry of which source answers for which
entity type rather than two that can drift.

## Status contract

| Situation | Response |
|---|---|
| All three rows known | 201, zero new calls |
| Source has the row | rows created and verified, 201 |
| Source loaded, no such row | 404, today's message; negative-cached |
| Budget at `SoftLimit` or beyond, metered lookup needed | 503 + `Retry-After` |
| Wall-clock budget exceeded | 503 + `Retry-After` |
| Source error (timeout, 5xx, parse) | 503, fixed category in `DiagnosticDetail` |
| Airline country name unresolvable | 404 naming the airline; nothing written |
| Traveler at the tracking cap | 409, before any provisioning, nothing written |
| `ReferenceProvisioning:Enabled` false | today's 404 |

**Provisioning is all-or-nothing.** If the airports resolve and the airline does not, nothing
is written and the response is 404.

This reverses an earlier decision in this design to keep partial success. The argument for
keeping it was that discarding resolved rows makes a retry pay for them again -- but that
argument does not survive contact with which sources actually cost money. Airports come from
`ourairports` and countries from `mledoze`, both free and both held in memory for 24 hours
after the first load, so re-fetching them on a retry costs effectively nothing. The only
metered source is `aeroapi`, for airlines -- and in the very scenario partial success was
meant to help, the airline is the thing that failed, so there is nothing metered to salvage.
What partial success would buy is near-zero; what it would cost is a second write path that
has to work out which entities are still safe to save once one of their countries could not
be resolved, and a reference table that can be left half-populated by a failed request.

One save, one transaction, nothing on failure.

Provisioning never throws out of the handler uncaught; every failure maps to a
`DomainException` with a status.

## Testing

- **Application.Tests** — `ReferenceDataProvisionerTests` against a fake registry: found,
  not-found and source-error; country inserted before airport; airline country-name
  resolution and its failure; a negative-cache hit skipping the lookup entirely; `SoftLimit`
  refusing the metered call; deadline exceeded → `Unavailable`. `TrackFlightCommandHandlerTests`
  for cap-before-provisioning and for each outcome's status.
- **API.Tests** — route-level 201/404/503 wiring with a stubbed provisioner.
  `FlightAssistantWebApplicationFactory` already gives both verification sources a throwing
  transport, so no test can accidentally download a dataset.
- **Infrastructure.Tests, PostgreSQL-backed** — that the foreign-key ordering genuinely holds
  and that the audit rows land under the `system` actor. The InMemory provider enforces no
  foreign key, so this class of bug is invisible to the API suite; that is what the #295
  harness exists for.

## Out of scope

- Filtering unknown airports out of search results. Search stays provider-backed and
  DB-independent.
- Backfilling reference data for flights already tracked.
- `Airline.CountryCode`'s longer-term future, which is #402.
