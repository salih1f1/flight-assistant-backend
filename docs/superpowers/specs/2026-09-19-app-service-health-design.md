# App Service Health — Design Spec

**Date:** 2026-09-19
**Status:** Approved in discussion, pending review of this document
**Issue:** #527

---

## Overview

The health page says **"Flight status sync: Running"**. On the dev App Service that is not reliably true.

`alwaysOn` is **false**, so App Service unloads the app after roughly twenty minutes without
requests, and `IHostedService` stops with it. Measured against the live resource:

| Hour (UTC) | Requests | HealthCheckStatus |
|---|---|---|
| 02:33 – 07:33 | 0, 0, 0, 0, 0, 0 | `None` |
| 08:33 onward | 30, 39, 16, 24, 26, 17 | `100` |

Six consecutive hours of nothing overnight, then both resuming when the working day started. The
jobs run while someone is using the panel and stop shortly after they close it. The panel says
otherwise.

This is the same class of falsehood the rest of this expansion has been removing: `aeroapi` reading
`Healthy` for a rejected key (#460), a refused cost query that would have rendered `$0.00` (#496).
Each is produced by working code and still tells the reader the opposite of the truth.

---

## Goals

- Say whether the background jobs will actually run, where that claim is made.
- Put the App Service in the data-source inventory like every other dependency.
- Need no configuration.

## Non-goals

- Metrics — `Http5xx`, request counts, `HealthCheckStatus`, charts. The Azure portal does that
  better and App Insights owns it. This answers one question.
- Anything that writes to Azure.
- Reporting on an App Service other than the one hosting the API.
- Changing the `alwaysOn` setting, or nagging to. Leaving it off is defensible for a dev
  environment: it is cheaper, and it caps what a forgotten test flight can cost. The panel's job is
  to make the state visible, not to call it wrong.

---

## 1. What is read

One ARM call:

```
GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{name}?api-version=2023-12-01
```

Through a named `HttpClient` with a bearer token from the existing `DefaultAzureCredential` — the
same shape as `AzureCostProvider`, and for the same reason: a named client is what
`DataSourceCatalogTests.EveryNamedHttpClientHasACatalogEntry` enumerates, so the dependency puts
itself on the health page instead of being invisible until someone notices (#460).

**Verify the api-version and every field path against Microsoft's current ARM reference before
implementing.** The table below is written from what `az webapp show` returned for the live
resource, which is good evidence that the values exist but not proof that a raw ARM `GET` returns
them at these paths — `az` may compose more than one call, and `siteConfig` in particular is
sometimes a separate sub-resource. If a path differs, use the real one and say so; if `siteConfig`
needs its own `GET /config/web`, make that call rather than reshaping the requirement.

This instruction exists because three design errors in #496 survived every test by being encoded
identically in the code and its fixtures. The binding requirement is "report whether Always On is
on", not the particular JSON path guessed here.

Five fields, all from that one response:

| Field | From | Why |
|---|---|---|
| `state` | `properties.state` | "Running", "Stopped" |
| `availabilityState` | `properties.availabilityState` | "Normal", "Limited", "DisasterRecoveryMode" |
| `enabled` | `properties.enabled` | a disabled site serves nothing |
| `alwaysOn` | `properties.siteConfig.alwaysOn` | **the one that changes what to believe about the jobs** |
| `instanceCount` | `properties.siteConfig.numberOfWorkers` | how many workers the plan runs |

### 1.1 No configuration

Resource group and site name come from `WEBSITE_RESOURCE_GROUP` and `WEBSITE_SITE_NAME`, which App
Service injects. The subscription id comes from the same resolver #523 added.

This is deliberate, and it is the lesson from #523: requiring `AzureCost:SubscriptionId` duplicated
something the platform already knew, at the cost of a deployment step that would be forgotten. None
of these three need asking for.

A developer machine has none of them, which correctly reads as `NotConfigured` rather than an error.

---

## 2. Where it shows

### 2.1 A data-source inventory row

Consistent with the other nine: display name, status, what is checked, and a link to the resource's
portal blade.

**`WhatIsChecked`:** "Reads the App Service's own state and whether Always On is enabled. Reports
only; it cannot change anything in Azure."

`Kind` `Infrastructure`, `Criticality` `Optional` — the API plainly runs without being able to read
its own resource metadata.

### 2.2 An amber note on the Jobs section — the part that matters

When `alwaysOn` is false, the Jobs section carries:

> The app is unloaded when idle, so these jobs pause and resume with traffic rather than running
> continuously. Turn on Always On in App Service to run them around the clock.

This is the whole point of the feature. A row elsewhere on the page stating `alwaysOn: false` would
be a true fact that nobody connects to the "Running" two sections above it. The correction belongs
where the false impression is created.

---

## 3. `alwaysOn: false` is not a failure

The App Service row stays **green**. Only the Jobs note is amber.

Turning Always On off is a legitimate choice — it is cheaper, and with the jobs idle a forgotten
test flight cannot poll overnight. A deliberate hosting decision must not paint a health page red,
which is the same rule the data-source inventory already follows for `NotConfigured` (#460) and the
job switches follow for an operator stop (#471).

What would be wrong is leaving it invisible while the page implies otherwise.

---

## 4. States

The vocabulary the Azure cost panel already uses, because this is the same class of dependency:

| State | When | Renders |
|---|---|---|
| `Ok` | the resource was read | the facts |
| `NotConfigured` | not running on App Service — no `WEBSITE_SITE_NAME` | gray |
| `NotAuthorized` | 401/403 | gray, naming **Reader** on the resource group |
| `Unavailable` | anything else — timeout, 5xx, a body we cannot read | amber |

`Reader` on `flight-assistant-dev` is already granted to the API's managed identity
(`83ad6f84-3ea0-4998-8c6b-203ae96184b8`), verified — so unlike #496, this ships with its role in
place and the happy path can be checked live on first deploy.

---

## 5. Caching

**Five minutes.** Site configuration changes rarely, and ARM read limits are generous but not
unlimited. Shorter than the cost panel's hour because `alwaysOn` is something an operator may
change and then immediately want to see reflected — the same reasoning that kept refusals uncached
in #496.

---

## 6. API

`GET /admin/health/app-service` → `AppServiceStatusDto`:

```
state, siteState, availabilityState, enabled, alwaysOn, instanceCount, siteName, message
```

`state` is the §4 enum; `siteState` is Azure's own "Running"/"Stopped", kept distinct so the two
are never confused. `message` carries the operator-facing explanation for non-`Ok` states,
sanitized to fixed categories plus facts this code controls — never a raw exception message,
following `VerificationSourceDownloader`'s rule.

---

## 7. Testing

The states, and the connection between them:

- `alwaysOn: false` → the Jobs section shows the note. `true` → it does not.
- **The note never appears when the App Service read failed.** `NotAuthorized` or `Unavailable`
  means we do not know whether Always On is on, and guessing either way is worse than silence.
- **`alwaysOn: false` does not degrade the App Service row's own status** — it stays `Ok`.
- Missing `WEBSITE_SITE_NAME` → `NotConfigured`, not an error.
- 401/403 → `NotAuthorized`, naming Reader.
- A malformed body → `Unavailable`, not a crash.
- The catalog completeness test picks the new client up; adding it without a catalog entry fails
  the build.

**The fixture is the real response** — `Basic`, `alwaysOn: false`, `state: Running`,
`availabilityState: Normal`, one worker — captured from the live resource, not invented.

That last point is the habit worth keeping. Three design errors in the Azure cost work survived
every test because the fixtures were written from the same assumptions as the code: a `dataAsOf`
field that does not exist, a `timePeriod` that means something other than what I assumed, and a
required setting duplicating what the platform already supplied. All three fell out of one look at
the real thing.

---

## 8. Delivery

1. **Backend** — provider, catalog entry, probe, endpoint, spec regeneration.
2. **Admin panel** — the inventory row and the Jobs note, after the backend merges and the client
   is regenerated.

---

## Risks

**The note could become noise.** If Always On is never turned on, the Jobs section carries an amber
line forever and people stop reading it. Mitigated by its wording: it states a consequence and names
the remedy, rather than nagging. If it does become noise, that is an argument for changing the
setting, not for hiding the note.

**One environment reports on itself.** If staging or production App Services are added, each
reports only on the one hosting it. That is correct, and worth knowing before someone expects the
dev panel to describe production.

---

## Open questions

None. Decisions taken in discussion: config facts only; the note on the Jobs section rather than a
row alone; `alwaysOn: false` green on its own row and amber only where it misleads; no
configuration.
