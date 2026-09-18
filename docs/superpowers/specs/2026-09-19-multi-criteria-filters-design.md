# Filtering a List by More Than One Criterion — Design Spec

**Date:** 2026-09-19
**Status:** Approved in discussion, pending review of this document
**Issue:** #476

---

## Overview

`GET /admin/data/{table}` takes one filter. The parameter is a single `string? Filter`, parsed as
`column:value` and split on the first colon:

```csharp
public (string Column, string Value)? ParsedFilter   // GetBrowsableDataQuery
{
    get
    {
        if (string.IsNullOrEmpty(Filter)) return null;
        var separator = Filter.IndexOf(':');
        return separator < 0 ? null : (Filter[..separator], Filter[(separator + 1)..]);
    }
}
```

That endpoint backs the admin panel's data browser, which is the list page for **every** entity —
airlines, airports, flights, tiers, verification requests. So no list in the panel can be narrowed
by two things at once. "Airlines in Turkey whose name contains *air*" is not expressible, and
neither is "unverified airlines in Turkey".

The one-slot limit also distorts the UI built on top of it. The panel's Verified / Unverified quick
toggle has to **wipe** any column filter when pressed, and says so:

```tsx
// The API takes one filter, so choosing one of these replaces any column filter.
```

The admin audit log is the one list that already works properly — `EntityType`, `EntityId`,
`ActorId` and an inclusive `FromUtc`/`ToUtc` range, all optional, all AND-combined. This spec brings
the data browser to the same place.

---

## Goals

- A list can be narrowed by several criteria at once, combined with AND.
- Every existing single-filter URL keeps working, byte for byte, with no change at any caller.
- Per-criterion matching semantics are exactly today's — no new grammar to learn or to test.
- The Verified toggle composes with column filters instead of replacing them.
- A filtered view stays shareable and reloadable through the URL, as it is today.

## Non-goals

- OR, and grouping of any kind. AND only.
- Saved or named filter views.
- Making CSV export follow the filtered view. Export sends the whole table today and continues to.
- Any change to sorting, paging, masking, or the reveal path.
- Any change to the audit log's filters, which already work this way.

---

## 1. The wire contract

`filter` becomes **repeatable**:

```
GET /admin/data/airlines?filter=Country:TR&filter=Name:air&sort=Name&Page=1&PageSize=25
```

Each occurrence is one criterion, in today's grammar, with today's meaning:

| Column kind | `value` | Match |
|---|---|---|
| string | `air` | case-insensitive *contains* |
| string | `*` | has a value: not null, non-empty after trimming (#385) |
| non-string | `TR`, `3`, `true` | exact, after conversion to the column's type |
| date | `>=2026-09-01` | on or after; a null date never matches (#334) |

Unchanged with it: the split on the *first* colon only (so an ISO-8601 value keeps its own colons),
case-sensitive column names checked against the exposed-column allowlist, and the refusal to filter
on a sensitive column.

**Criteria AND together.** Criteria naming different columns AND; repeats of the same column also
AND, so `?filter=Name:air&filter=Name:tur` means both substrings must be present. That falls out of
the implementation rather than being a feature in itself, and there is no reason to forbid it.

**Cardinality.** Zero criteria is the unfiltered call. One criterion is today's call, exactly.
More than **10** is a 400 — a bound on how much query a single URL can compose, far above what a
form that adds one criterion at a time will ever produce.

### Why a repeated parameter

The alternative was one parameter carrying a separated list (`?filter=Country:TR,Name:air`). These
values are airline names, city names and free-typed text, where commas and semicolons are ordinary
characters, so a separator demands an escaping rule — and an escaping bug here does not throw, it
silently returns the wrong rows. A repeated parameter needs no escaping: each criterion owns its own
parameter and may contain anything.

A JSON-valued `filters` parameter was also considered and rejected. It is the natural home if OR and
grouping ever arrive, but it makes URLs unreadable and un-typeable, and would leave the API carrying
both `filter` and `filters` forever for the sake of the existing links.

### Backward compatibility

A single `filter=` is simply a list of one. These callers are therefore untouched by this change:

| Caller | URL it builds |
|---|---|
| Dashboard stat tiles (`StatsSection`) | `/data/{table}?filter={filter}` |
| Needs-attention links (`NeedsAttentionSection`) | `/data/{slug}?filter=VerificationIssue:*` |
| Pending-queue link (`pendingVerifications`) | `/data/verification-requests?filter=Status:Pending` |
| Traveler → flights (`TravelerDetailPage`) | `/data/traveler-flights?filter=TravelerId:{id}` |
| Flight → status events (`FlightDetailPage`) | `/data/flight-status-events?filter=FlightId:{id}` |
| Health report's pending count (`HealthReport`) | direct API call, `filter: 'Status:Pending'` |

Any link a user has bookmarked or shared keeps working for the same reason.

---

## 2. Backend

### `GetBrowsableDataQuery`

`Filter` becomes a list, and its parse property follows:

```csharp
public sealed record GetBrowsableDataQuery(
    string Table,
    PagedRequest Paging,
    string? Sort = null,
    IReadOnlyList<string>? Filters = null) : IRequest<PagedResult<Dictionary<string, object?>>>
{
    /// <summary>Each filter split into its column and value halves on the first ':'. A criterion
    /// carrying no colon is dropped here and rejected by the validator with a 400 before this is
    /// ever read for real.</summary>
    public IReadOnlyList<(string Column, string Value)> ParsedFilters => …;
}
```

`ParsedFilter` (singular, nullable) disappears; nothing outside this file and its tests reads it.

### The endpoint

```csharp
string? sort,
string[]? filter,          // was: string? filter
…
await mediator.Send(new GetBrowsableDataQuery(table, paging, sort, filter), ct);
```

Minimal APIs bind a repeated query parameter to an array natively, so the parameter keeps its name
and no custom binder is needed. The endpoint's OpenAPI description gains a sentence on repetition
and the AND rule, and states the cap.

### Validation

`GetBrowsableDataQueryValidator`'s two filter rules become per-criterion:

- Every criterion must contain a `:`.
- Every criterion's column must be exposed for this table and not sensitive.
- At most 10 criteria.

**Each message names the criterion it is about** — `Filter 'Cuntry:TR' names an unknown column
'Cuntry'. Valid columns: …` — because with several in play, "invalid filter column" alone does not
say which one to fix. Every failing criterion is reported, not just the first, since FluentValidation
is already collecting them.

The existing deliberate behaviour is preserved: an unresolved table slug is treated as valid here,
because table existence is a 404 decided by the endpoint and this validator only ever produces 400s.

### The repository

`IBrowsableDataRepository.QueryAsync` trades its `string? filterColumn, string? filterValue` pair for
`IReadOnlyList<(string Column, string Value)> filters`, and `QueryGenericAsync` folds them:

```csharp
IQueryable<TEntity> query = _db.Set<TEntity>().AsNoTracking();

foreach (var (column, value) in filters)
    query = ApplyFilter(query, column, value);

// Total must be counted after the filters above and before Skip/Take.
var total = await query.CountAsync(ct);
```

Chained `Where` calls are ANDed by EF into one SQL statement, which is exactly the required
semantics and needs no expression-tree work of our own.

**`ApplyFilter` and `ConvertFilterValue` are not touched.** Every value semantic — string contains,
`*`, dates and `>=`, enums, bools, nullable columns — keeps its current implementation and its
current tests. The change is only in how many times that method is called.

The `BuildQueryString` test seam takes the same list and folds it the same way. `GetValueAsync` and
the id-lookup path are untouched: they never used the filter parameters.

`QueryAsync` has exactly one production caller — `GetBrowsableDataQueryHandler` — and about twenty
call sites in `BrowsableDataRepositoryTests`. Those are mechanical updates (`null, null` becomes an
empty list, `"Name", "air"` becomes a one-element list) and must stay mechanical: an existing test
that changes meaning while its signature is being updated is a regression hiding in churn.

---

## 3. Panel: URL and view state

The query parameter keeps the name `filter` and is read with `getAll`:

```ts
const filters = searchParams.getAll('filter')     // was: searchParams.get('filter') ?? undefined
```

so an incoming single-filter link is read as a list of one, and every link in the table above lands
on a working filtered view exactly as now.

`updateView` takes `filters?: string[]` and writes one `filter` entry per criterion, deleting the key
entirely when the list is empty. Its existing rule stands: **any change other than paging resets to
page 1** — a filter that narrows the set to three rows must not leave you stranded on page 7.

The selection `viewKey` (`table|page|sort|filter`) joins all criteria, so a filter change keeps
dropping the checkbox selection and closing the reveal and verify state, for the same reason as
today: those belong to the rows that were on screen.

The generated client takes `filter` as `string[]`, so React Query keys each distinct criteria set
separately and two different filtered views do not share a cache entry.

---

## 4. Panel: the filter UI

```
Country: TR ×   Name: air ×   CreatedUtc: on or after 2026-09-01 ×     [Clear filters]

[Column ▾] [Comparison ▾] [Value_______] [ Add filter ]
```

**Chips.** Each applied criterion renders as a chip whose × removes *only* that criterion. A date
criterion carrying `>=` reads as "on or after", matching the words its own comparison picker uses,
rather than showing the raw operator.

**The form.** The existing column / comparison / value row stays as it is, including the
kind-driven control choice from `filterColumns.ts` (enum picker, bool picker, date input, text, and
"this column can't be filtered" for `Unsupported`). Its submit button reads **Add filter** and
*appends* to the applied list instead of replacing it; after an add, the row resets to a fresh
column pick, as it does today after an apply.

**Duplicates.** Adding a criterion identical to one already applied is a no-op — no second chip, no
navigation. Two criteria that differ in value are, of course, both kept.

**Clear filters** removes every criterion, and is shown only when at least one is applied.

**The Verified toggle** stops being exclusive. All / Verified / Unverified now add or remove the
`IsVerified:true` / `IsVerified:false` criterion and leave every other criterion standing; *All*
removes only that criterion. Its pressed state is computed from whether that criterion is present,
not from the whole filter string being equal to it. The comment quoted at the top of this spec goes
with it.

The form remounts on a change to the table or to the applied criteria, as it does today, so its
inputs always reflect what is actually applied.

---

## 5. Testing

**Backend**

- `ParsedFilters` over zero, one and several criteria, and over a value containing colons.
- Validation: a criterion with no colon; an unknown column; a wrong-case column; a sensitive column;
  a valid criterion alongside an invalid one — the message names the offending criterion, and every
  failing criterion is reported.
- The 10-criteria cap: 10 passes, 11 is a 400.
- Handler: the parsed list reaches the repository intact and in order.
- Repository, against Postgres via `BuildQueryString`: two criteria produce one statement carrying
  both predicates, ANDed. This is the test that proves the fold actually composes rather than the
  last criterion winning.
- Repository: `Total` is counted with all criteria applied, not with the first or with none.
- Endpoint: `?filter=a:b&filter=c:d` returns only rows matching both.
- Regression: a single `filter=` returns exactly what it returns today, for a string column, a
  non-string column, `*` and a `>=` date.

**Panel**

- Two `filter` params render two chips; × removes one and leaves the other applied.
- Add filter appends to an applied criterion rather than replacing it.
- Adding a duplicate criterion changes nothing.
- The Verified toggle applied over a column filter keeps the column filter; *All* removes only
  `IsVerified`; the pressed state follows the criterion's presence.
- An old single-`filter` link still filters, and its criterion shows as a chip.
- Clear filters empties the list and returns to page 1.
- A filter change resets paging and drops the checkbox selection.

Per the project's standing rule, each test is proven by breaking the code it guards — a break
derived from the code as written, not guessed in advance — and the break is shown failing before the
code is restored.

---

## 6. Sequencing

1. **Backend PR** (this repo): the contract, the validator, the repository fold, the OpenAPI text,
   and the tests above.
2. **Panel PR** (`flight-assistant-admin-panel`): regenerate the client from the updated OpenAPI
   document, then the URL handling, the chips and the Verified toggle.

The panel PR merges after the backend PR, as with the track-flight pair (#341 / panel #72): its
regenerated client describes a `filter` the deployed API does not accept until the backend ships.
