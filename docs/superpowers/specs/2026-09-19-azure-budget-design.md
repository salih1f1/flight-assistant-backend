# Azure Subscription Budget and Spend — Design Spec

**Date:** 2026-09-19
**Status:** Approved in discussion, pending review of this document
**Issue:** #496

---

## Overview

The admin panel shows what FlightAware costs — the AeroAPI gauge, `$0.89 of $5.00`. It says nothing
about what the project costs to run in Azure.

That gap has already bitten. On 2026-09-17 the subscription was disabled and dev API deploys failed
for two days; the panel showed nothing, because nothing in it watches Azure spend. The one number
that would have predicted it was in the portal, which nobody was looking at.

This adds the subscription's budget and current spend beside the AeroAPI gauge: one panel answering
"what is this costing, and how close is it to the limit".

---

## Goals

- Show the **real** Azure budget — the Budget resource defined in Cost Management, not a number
  duplicated in config.
- Show month-to-date subscription spend against it.
- Be honest about how old the figure is.
- Degrade clearly when the role is not granted, or no budget exists.
- Register as a data source, so it appears on the health page like every other dependency.

## Non-goals

- Per-service or per-resource-group breakdown.
- Forecasting month-end spend. (The AeroAPI panel deliberately declined this for bursty spend; Azure
  spend is steadier, but a forecast is still a claim about the future on a page of measurements.)
- Cost alerts, budget creation, or anything that writes to Azure.
- App Service instance health — that is issue #495, piece 4, separate.

---

## 1. Reaching Cost Management

A named `HttpClient` calling the Cost Management REST API directly, with a bearer token from the
existing `DefaultAzureCredential`. Registered like every other outbound client, ending the chain
with `.AddDataSourceObservation(DataSourceKeys.AzureCost)` — called last so the handler sits
innermost and sees each transport result rather than the resilience pipeline's verdict after
retries.

**Deliberately not `Azure.ResourceManager.CostManagement`.** The reason is not style. Every outside
dependency in this application is reached through a named `HttpClient`, and
`DataSourceCatalogTests.EveryNamedHttpClientHasACatalogEntry` fails the build when a named client
has no catalog entry — it builds the real container and enumerates the client names
`AddInfrastructure` actually registered, so a hand-maintained list cannot drift out of step with it. An SDK call
registers no client, so Azure would be invisible on the health page and nothing would catch it.
That is precisely the failure that test exists to prevent (#460: six dependencies invisible because
nothing forced them to appear). Using an `HttpClient` makes this one register itself.

`DefaultAzureCredential` is already in the application, used for Key Vault (`Program.cs`), so this
adds a token scope, not a credential mechanism.

### 1.1 The two calls

| What | Call |
|---|---|
| The budget | `GET /subscriptions/{id}/providers/Microsoft.Consumption/budgets?api-version=2023-05-01` |
| Month-to-date spend | `POST /subscriptions/{id}/providers/Microsoft.CostManagement/query?api-version=2023-03-01` |

The budget resource carries its own `currentSpend`, which is usually enough on its own. The query is
what answers "spend this month" when **no budget is defined**, so the panel can still show a cost
figure rather than nothing. Both are read-only.

### 1.2 Configuration

**Corrected after implementation** (see the note at the end of this document): shipped as
`AzureCost:*`, not `Azure:*` as first written below. `appsettings.json` already has an `Azure`
section holding the B2C sign-in policy, and mixing cost settings into it would put two unrelated
concerns under one key, so a separate section keeps them apart.

```
AzureCost:SubscriptionId — required for this feature; absent means "not configured"
AzureCost:BudgetName     — optional; when set, that budget is used rather than the first returned
```

Absent `SubscriptionId` is a normal state, not an error. A local developer has no subscription and
must not see a red panel because of it.

---

## 2. Freshness — the thing most likely to mislead

**Azure cost data lags, typically 8–24 hours.** This panel will sit directly beneath the AeroAPI
gauge, which shows near-live spend. Two cost figures side by side invite the reader to assume they
mean the same kind of thing, and they do not.

**Corrected after implementation** (see the note at the end of this document): neither Cost
Management endpoint this reads — the Consumption Budgets list (2023-05-01) or the Cost Management
query (2023-03-01) — reports its own last-refreshed timestamp anywhere in its response. That was
assumed, not verified, when this section was first drafted; task 2 checked both REST references
field-by-field and found no such field at any nesting. So:

- `dataAsOfUtc` is always null. The lag itself is real — Azure genuinely reports cost 8–24 hours
  late — it is just unmeasurable from either response, so this never invents a value to fill it.
- The panel says, in words, that Azure reports on a delay.
- With no as-of timestamp available, the panel says the age is unknown rather than implying the
  figure is current.

A figure a day old, presented as current, is worse than no figure: it invites a decision on stale
information while looking authoritative. Fabricating an as-of value — stamping the fetch time, say
— would have exactly that defect, which is why null is left null rather than backfilled from the
clock.

---

## 3. States

The panel's correctness is mostly in its states, not its arithmetic.

| State | When | Renders |
|---|---|---|
| `Ok` | budget and spend both read | the gauge |
| `NoBudget` | Cost Management returned no budget | spend, and "no budget defined in Azure" |
| `NotConfigured` | `AzureCost:SubscriptionId` absent | gray, "not configured" |
| `NotAuthorized` | 401/403 | gray, naming **Cost Management Reader** |
| `Unavailable` | anything else — timeout, 5xx, malformed | amber, with the category |

**`NotAuthorized` is gray, not red, and never `$0.00`.** A missing role assignment is a setup state,
not a fault — the same rule the data-source inventory follows for `NotConfigured`. Rendering zero
spend because we were refused would be a lie that looks like good news, on a panel whose whole
purpose is to warn.

`NotAuthorized` names the role because "access denied" without naming what to grant is a dead end.

---

## 4. Caching

**One hour**, against AeroAPI's ten minutes.

The underlying data updates every few hours at best, and Cost Management rate-limits aggressively —
its query endpoint is documented as low-throughput and returns 429 under modest load. A shorter
window would spend quota re-fetching a number that has not moved.

Cached server-side, the same shape as `GetAeroApiUsageQuery`'s ten-minute cache, so a dashboard
refresh costs nothing.

---

## 5. API

`GET /admin/usage/azure` → `AzureBudgetDto`:

```
state, budgetName, budgetAmount, currentSpendAmount, currency,
periodStartUtc, periodEndUtc, dataAsOfUtc, message
```

- `state` is the enum from §3. The panel renders what it is given rather than re-deriving it, the
  same split the data-source inventory uses.
- `currency` is whatever Azure reports. **Never assume USD** — the subscription's billing currency
  is a property of the account, and formatting EUR as dollars would misstate the number by a
  meaningful factor.
- `message` carries the operator-facing explanation for the non-`Ok` states, sanitized to fixed
  categories plus facts this code controls — never a raw exception message, following
  `VerificationSourceDownloader`'s rule.

---

## 6. Data source registration

**Corrected after implementation** (see the note at the end of this document): shipped as
`azurecost`, not `azure-cost` as first written below — matching every other one-word catalog key
(`aeroapi`, `github`, `mledoze`, …). `DataSourceKeys` is a primary key and appears in URLs, so this
document is corrected to match the shipped spelling rather than the code changed to match it, which
would cost a migration.

One catalog entry, `azurecost`:

- **Kind** `Infrastructure`, **Criticality** `Optional` — the application runs fine without it.
- **WhatIsChecked:** "Reads the subscription's budget and month-to-date spend from Azure Cost
  Management. Reports only; it cannot change anything in Azure."
- **ExternalUrl:** the subscription's Cost analysis blade.
- **SupportsProbe:** true, with no cost note — Cost Management reads are not billed.

This is what makes the completeness test cover it, and what puts Azure on the health page beside
every other dependency.

---

## 7. Panel

A section on the **Dashboard, directly below AeroAPI usage**. Both answer "what is this costing",
and an operator checking spend wants them in one glance; `/health` stays about health.

- Spend against budget, with the same bar treatment as the AeroAPI gauge, so the two read as a pair.
- The period, and the as-of line from §2.
- Non-`Ok` states render as one gray or amber line, never a gauge — a bar drawn from a figure we do
  not have is the failure mode this section is most exposed to.
- Amounts formatted in the reported `currency`.

---

## 8. Testing

**The states, not the arithmetic.** Each of the five in §3 gets a test: no budget defined; 403 →
`NotAuthorized` naming the role; absent `SubscriptionId` → `NotConfigured`; a 500 → `Unavailable`;
a timeout → `Unavailable`.

Also:

- **A non-USD currency is rendered in that currency**, not dollars.
- **A budget period that is not a calendar month** — Azure budgets can be quarterly or annual — has
  its real period reported rather than being described as "this month".
- **`dataAsOfUtc` is always null, and stays null even against a fully-populated response.**
  Corrected after implementation (see the note at the end of this document) from the originally
  planned "comes from the response, not the clock" test, which assumed a field neither endpoint
  actually returns (§2). `DataAsOfUtc_IsAlwaysNull_NeitherAzureEndpointReportsIt` feeds the
  provider a response carrying every optional field the 2023-05-01 reference documents — a real
  `eTag`, a `forecastSpend` — and still asserts null, proving this isn't merely absent from a thin
  fixture. `AMissingAsOfTimestamp_IsNull_NotNow` pins that a missing timestamp is never backfilled
  from the clock either.
- The panel draws **no gauge** in any non-`Ok` state.
- `DataSourceCatalogTests` picks the new client up automatically; adding it without a catalog entry
  must fail the build.

Happy-path tests run against recorded Cost Management responses. Live verification needs the role
(§9).

---

## 9. What cannot be verified until a role is granted

Every call returns 403 until the API's managed identity has **Cost Management Reader** on the
subscription. That is a portal or CLI grant, outside this repository.

So this ships **verified by test, not verified live**: the `NotAuthorized` path can be exercised
properly, the happy path only against recorded responses. The first real numbers appear when the
role is granted, and that is the moment to check the figures against the portal.

This is stated here so it is not discovered at review.

---

## Open questions

None. Decisions taken in discussion: the real Azure Budget resource rather than a configured number;
whole-subscription scope; `HttpClient` over the SDK so the completeness test covers it; Dashboard
placement beneath the AeroAPI gauge.

---

## Corrections after implementation

Recorded here, rather than silently edited away, so the history of what this document originally
claimed is not lost — this is what the final review before the #496 pull request found and why.
The Phase 2 issue in the panel repository should be written from the corrected text above, not from
memory of the original.

- **§2 (Freshness).** Originally claimed the response carries the data's own as-of timestamp. It
  does not: neither Cost Management endpoint reports one anywhere in its response (verified against
  both REST references, field-by-field, in task 2) — an assumption that turned out false, not a
  decision that changed. The honest part of the original claim stands: the 8–24 hour lag is real,
  just unmeasurable from the response, so the panel says "age unknown" instead of a number.
- **§8 (Testing).** The test named "`dataAsOfUtc` comes from the response, not the clock" —
  asserted with a fake clock — cannot exist: there is no response field for it to read from.
  Replaced with a description of the two tests actually built, which pin "always null" instead,
  including against a response with every other optional field populated.
- **§1.2 (Configuration).** Named `Azure:SubscriptionId` / `Azure:BudgetName`. Shipped as
  `AzureCost:SubscriptionId` / `AzureCost:BudgetName`, because `appsettings.json`'s existing `Azure`
  section already holds the B2C sign-in policy, and cost settings belong in a section of their own
  rather than mixed into it. §3's states table repeated the original spelling and is corrected the
  same way.
- **§6 (Data source registration).** Named the catalog key `azure-cost`. Shipped as `azurecost`,
  matching every other one-word catalog key. `DataSourceKeys` is a primary key and appears in URLs,
  so the document was corrected to match the code rather than the other way around — renaming it
  now would cost a migration for no behavioural gain.
