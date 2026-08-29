# Diagnostic Error Responses for Upstream Provider Failures

**Date:** 2026-08-29
**Status:** Approved

## Problem

A flight search against AviationStack currently fails with an opaque response:

```json
{
  "title": "Flight search is temporarily unavailable due to a server configuration issue.",
  "status": 500,
  "instance": "/flights/search"
}
```

Nothing in the body says what went wrong, and there is no identifier to correlate the
response with a log line. Diagnosing it required reproducing the upstream call by hand.

The underlying failure in this case: AviationStack rejects any date filter on `/v1/flights`
on the current subscription plan, returning HTTP 403 with
`{"error":{"code":"function_access_restricted", ...}}`. Confirmed by isolating the request
parameter by parameter — the same request without a date parameter returns 200. This is a
plan limitation, not a code defect, and is being resolved by a plan upgrade. This spec covers
only the diagnosability problem it exposed.

A second instance of the same problem: `AviationStackFlightStatusProvider` has no error
handling at all (`if (!response.IsSuccessStatusCode) return null;`) and no logger. Upstream
failures are silently swallowed and reported to callers as "no status available."

## Goals

- An operator can tell **why** a request failed from the response body alone, in non-production
  environments.
- Every error response carries a `traceId` for correlating with logs in all environments.
- Production continues to expose no provider, plan, or credential detail to end users.
- Both AviationStack providers share one error-handling path.

## Non-goals

- A stable client-facing error-code taxonomy. The audience for this change is the operator
  debugging, not the iOS client's branching logic.
- Changing which HTTP status codes map to which upstream error codes. The existing mapping
  (503 throttle / 500 config / 400 validation / 502 other) is correct and stays.
- Fixing the plan limitation itself.

## Design

### 1. Plumbing: carrying diagnostic detail

`DomainException` gains one optional property, set via new optional constructor parameters so
every existing call site compiles unchanged:

```csharp
public string? DiagnosticDetail { get; }
```

`GlobalExceptionHandler` takes `IHostEnvironment` and, across **all four** branches
(FluentValidation, `DomainException`, `CsvHelperException`, unhandled):

- always sets `Extensions["traceId"] = Activity.Current?.Id ?? ctx.TraceIdentifier`
- for `DomainException`, sets `Detail = de.DiagnosticDetail` **only when `!env.IsProduction()`**

Environment logic lives in the API layer; the Domain layer stays HTTP- and
environment-agnostic.

**Rejected alternatives.** A dedicated `UpstreamProviderException : DomainException` with
typed provider/code fields is more expressive but invents a type for a single current use
case; revisit if a second provider is added. Gating inside the providers via `IHostEnvironment`
puts presentation logic in Infrastructure and cannot supply a `traceId`.

### 2. Shared AviationStack response handling

New file `FlightAssistant.Infrastructure/ExternalServices/AviationStack/AviationStackResponse.cs`:

```csharp
/// Reads an AviationStack response, throwing DomainException for any provider-side
/// failure. Returns the parsed document only on success.
internal static Task<JsonDocument> ReadOrThrowAsync(
    HttpResponseMessage response,
    string operation,   // "Flight search" | "Flight status"
    string context,     // "SOF->IST 2026-10-10" | "TK1030 2026-10-10"
    ILogger logger,
    CancellationToken ct)
```

It absorbs the existing sequence from `AviationStackFlightSearchProvider` lines 74–108 (read
body → parse → inspect `error` object → check status code) plus the `MapUpstreamError` switch.
`operation` supplies the message prefix so each caller's wording stays specific. `context`
appears in logs only.

Public `Message` values are unchanged from today's search provider, with one exception: the
config-error case becomes *"{operation} is unavailable due to a flight data provider
configuration issue."* — "temporarily unavailable" is misleading for a plan restriction, which
is not transient.

Every mapped exception sets:

```csharp
DiagnosticDetail = $"AviationStack returned HTTP {(int)status} {code}: {providerMessage}"
```

**The request URL is never included**, because it carries `access_key`. Only the HTTP status,
the upstream error code, and the upstream message are included. This keeps the existing
`SanitizeAviationStackTelemetryProcessor` redaction sufficient.

### 3. Provider changes

`AviationStackFlightSearchProvider`: lines 74–108 and `MapUpstreamError` are replaced by a
`ReadOrThrowAsync` call. `BuildUrl`, `TryMap`, and the parsing helpers are untouched.

`AviationStackFlightStatusProvider`:
- gains `ILogger<AviationStackFlightStatusProvider>` via constructor injection
- drops `if (!response.IsSuccessStatusCode) return null;` in favour of `ReadOrThrowAsync`
- still returns `null` when the `data` array is absent or empty — that is genuinely "no status
  yet", not a failure, and `SyncFlightStatusCommand` continues to treat it as `false`
- applies `Uri.EscapeDataString` to `_apiKey` and `flightNumber` when building the query,
  matching the search provider

Throwing from the status provider is safe for the background sync:
`FlightStatusSyncBackgroundService.RunBatchAsync` already wraps each flight's
`SyncFlightStatusCommand` in try/catch and logs, so one failing flight cannot abort the batch.
The behavioural change is that a genuine upstream failure now surfaces as an error on the
manual sync endpoints and as a logged error per flight in the background loop, instead of
being reported as "not updated".

## Example output

Development / Staging:

```json
{
  "title": "Flight search is unavailable due to a flight data provider configuration issue.",
  "status": 500,
  "instance": "/flights/search",
  "detail": "AviationStack returned HTTP 403 function_access_restricted: Your current subscription plan does not support this API function.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

Production — unchanged from today apart from the added `traceId`:

```json
{
  "title": "Flight search is unavailable due to a flight data provider configuration issue.",
  "status": 500,
  "instance": "/flights/search",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

## Testing

| Project | Test |
|---|---|
| `Infrastructure.Tests` | All 11 existing `AviationStackFlightSearchProviderTests` test methods pass unchanged, including `SearchAsync_Throws500_OnConfigErrors_WithoutLeakingCause` — `Message` is untouched, the cause rides in `DiagnosticDetail` |
| `Infrastructure.Tests` | `DiagnosticDetail` carries the upstream code and message |
| `Infrastructure.Tests` | `DiagnosticDetail` never contains `access_key` |
| `Infrastructure.Tests` | New status-provider tests: throws `DomainException` on a 403 error body; returns `null` on empty `data` |
| `API.Tests` | New `Middleware/GlobalExceptionHandlerTests`: `traceId` emitted on every branch |
| `API.Tests` | `Detail` present when environment is Development; absent when Production |

`FlightAssistant.API.Tests` has no `Middleware` directory yet; it is created following the
layout of the existing test directories.

## Risks

- **Behavioural change on status sync.** Errors that were silently swallowed now surface.
  Expected and intended, but the background service will log an error per tracked flight per
  cycle while a provider-wide failure persists. Acceptable: the per-flight try/catch already
  bounds the blast radius, and the noise is the signal.
- **Staging exposes upstream detail.** Deliberate — Staging is not public. If that changes,
  the gate is a one-line change to `IsProduction()`.
