# A Program's Alliance Comes From Its Airline — Design Spec

**Date:** 2026-09-19
**Status:** Approved in discussion, pending review of this document
**Issue:** #504
**Panel issue:** to be opened once this merges

---

## Overview

Two columns answer "which alliance is this program in", and they are not equal citizens:

```csharp
// AirlineConfiguration — a real foreign key to alliances, per #449
public string? AllianceCode { get; private set; }   // Airline
public Alliance? Alliance { get; private set; }

// LoyaltyProgramConfiguration — a bare string, no foreign key
b.Property(x => x.AllianceCode).HasMaxLength(8);    // LoyaltyProgram
```

`airlines.AllianceCode` is refused at create, update and import by
`ReferenceExistence.EnsureAllianceExistsAsync` unless the code names a row in `alliances`.
`loyalty_programs.AllianceCode` is validated only as "at most 8 characters" — free text.

The drift that allows is already in the repository. The entity comment and both panel forms offer
`"STAR" / OW / ST`; the alliance codes actually seeded are `SA / OW / ST`. `STAR` names no alliance
and never has.

An alliance is an *airline* alliance. A program belongs to an airline. So the program's alliance is
the airline's, and holding a second copy on the program can only ever agree or be wrong.

**Nothing reads the program-level column for behaviour.** Lounge eligibility reads
`LoyaltyTier.AllianceCode` and `LoyaltyTier.AllianceTierCode` (`LoungeEligibilityService`);
verification never compares a program's alliance (`Verification/` names no `LoyaltyProgram`). The
column reaches DTOs, the CSV export and the browse columns, and stops there.

## Goals

- `LoyaltyProgram.AllianceCode` is gone from the domain, the database, and every contract.
- A program's alliance is its airline's, in every read that shows one.
- No alliance fact currently in the system is lost: what the programs assert moves down onto the
  airlines before the column is dropped. The two exceptions — codes naming no alliance, and the
  two programs whose airline does not exist — are named under *Accepted consequences*.
- A program with no airline has no alliance, and says so rather than looking like missing data.

## Non-goals

- `LoyaltyTier.AllianceCode` / `AllianceTierCode`. Lounge eligibility reads them, and they are the
  same duplication one level down. Separate issue, after this one.
- Filling in alliances for the other ~650 seeded airlines. A data job, not this change.
- Any change to lounge eligibility, verification, or the alliance→airlines page (#174).
- Modelling an alliance-wide program that belongs to no airline. See *Accepted consequences*.

---

## The rule

> A program's alliance is the alliance of its airline. A program with no airline has no alliance.

There is no override, no fallback and no second place to write one. This is the whole of the
change; everything below is what it costs to get there.

---

## The catch: the data lives on the wrong table

The alliance facts are currently held **only** by programs:

- All **692** seeded airlines have `allianceCode: null`.
- **21 of 25** seeded programs carry an alliance code.

Drop the column first and the system forgets every alliance it knows, including on the
alliance→airlines page shipped in #174. So the data moves down first, in the same pull request, in
two parts — one for databases that already exist, one for databases created later.

### Part 1 — the seed, for fresh databases

`airlines.v1.json` gains `AllianceCode` on the 21 airlines the loyalty seed already asserts one
for. This is not a new claim; it is the same claim written on the row that owns it:

| IATA | Alliance | Airline (row in file) | IATA | Alliance | Airline (row in file) |
|------|----------|-------------------|------|----------|-------------------|
| BA | OW | British Airways (232) | QF | OW | Qantas (33) |
| DL | ST | Delta Air Lines (611) | VS | ST | Virgin Atlantic Airways (247) |
| UA | SA | United Airlines (666) | JL | OW | Japan Airlines (335) |
| AA | OW | American Airlines (593) | NH | SA | All Nippon Airways (329) |
| AF | ST | Air France (210) | IB | OW | Iberia Airlines (192) |
| LH | SA | Lufthansa (158) | AY | OW | Finnair (201) |
| QR | OW | Qatar Airways (465) | A3 | SA | Aegean Airlines (257) |
| SQ | SA | Singapore Airlines (539) | TP | SA | TAP Portugal (460) |
| TK | SA | Turkish Airlines (566) | MH | OW | Malaysia Airlines (404) |
| AC | SA | Air Canada (76) | TG | SA | Thai Airways Intl (555) |
|    |    |                       | ET | SA | Ethiopian Airlines (198) |

Every row above was matched by IATA against `airlines.v1.json`; none was ambiguous.

**Two programs name an airline the seed does not contain**: `CX-MPC` (Cathay Pacific, OW) and
`KE-SKY` (Korean Air, ST). Neither airline is in the 692 rows under any name or code — the seeder
already logs *"airline IATA 'CX' not found; seeding without airline link"* for them today. So both
rows are added to `airlines.v1.json`, carrying their alliance like the rest:

```json
{ "Name": "Cathay Pacific", "Iata": "CX", "Icao": "CPA", "CountryCode": "HK", "AllianceCode": "OW" },
{ "Name": "Korean Air",     "Iata": "KE", "Icao": "KAL", "CountryCode": "KR", "AllianceCode": "ST" }
```

`AirlineSeedDto` reads only `Iata`, `Icao`, `Name`, `CountryCode` and `AllianceCode`. The `Id`,
`Callsign` and `Active` fields every existing row carries are ignored — in particular the file's
`Id` is **not** the database id the migration joins on, and the numbers in the table above are
there to locate rows in the file, nothing more. Follow the file's shape for consistency if you
like; the seeder reads none of it.

Without them, dropping the column leaves those two programs with neither an airline nor an
alliance. The seeder inserts an airline only when no row matches its IATA or ICAO
(`ReferenceDataSeeder.PopulateAirlinesAsync`), so adding rows is safe to re-run.

### Part 2 — the migration, for databases that exist

Copy up, then drop:

```sql
UPDATE airlines a SET "AllianceCode" = p."AllianceCode"
FROM loyalty_programs p
WHERE p."AirlineId" = a."Id"
  AND a."AllianceCode" IS NULL
  AND EXISTS (SELECT 1 FROM alliances al WHERE al."Code" = p."AllianceCode")
  AND p."Id" = (SELECT MIN(q."Id") FROM loyalty_programs q
                WHERE q."AirlineId" = a."Id" AND q."AllianceCode" IS NOT NULL);

ALTER TABLE loyalty_programs DROP COLUMN "AllianceCode";
```

Four properties, each deliberate:

- **An airline's own alliance is never overwritten.** `a."AllianceCode" IS NULL` — a value an admin
  set on the airline outranks anything a program claims.
- **A code that is not a real alliance cannot be copied.** The `EXISTS` guard matches the foreign
  key on `airlines.AllianceCode`; without it the statement would fail outright on one junk value.
  Such values — `"STAR"`, if anyone typed it — are discarded with the column. They reference
  nothing, so nothing is lost that could be relied on.
- **Ties are deterministic.** Where two programs of one airline disagree, the lowest program id
  wins. #178 stopped the panel offering an airline a second program, so this guards historical rows
  only.
- **`Down` re-adds the column, empty.** Nullable, no values restored. Rolling back the schema
  cannot recover a duplicate that, by then, has a better home on the airline.

---

## What is removed

| Layer | File |
|---|---|
| Domain | `LoyaltyProgram` — field, `Create`, `CreateSeeded`, `Update` |
| Persistence | `LoyaltyProgramConfiguration` |
| Admin browse | `BrowsableTables` — the `loyalty-programs` column list |
| Contracts | `LoyaltyProgramDto`, `CreateLoyaltyProgramRequest`, `UpdateLoyaltyProgramRequest` |
| Commands | `CreateLoyaltyProgramCommand`, `UpdateLoyaltyProgramCommand` + validators |
| Import | `ImportLoyaltyProgramsCommand` and `LoyaltyProgramImportDto` |
| Export | `ExportLoyaltyProgramsQuery` |
| Queries | `GetLoyaltyProgramsQuery`, `SearchLoyaltyProgramsQuery` (both `…Result` records) |
| Endpoints | `AdminLoyaltyEndpoints`, `LoyaltyEndpoints` |
| Seed | `ReferenceDataSeeder.LoyaltyProgramSeedDto`, `loyalty-programs.v1.json` (program level only) |

Tier-level `allianceCode` stays in `loyalty-programs.v1.json` and everywhere else.

### The one outward-facing break

`LoyaltyProgramDto.allianceCode` is on the **public** `/loyalty` endpoints, not just the admin ones.
It is removed rather than kept as a value computed from the airline, because keeping it would leave
the contract still saying a program has an alliance of its own.

**Assumption, stated because it was not confirmed:** the admin panel is the only client of those
endpoints. If a mobile or other client reads the field, the fix is small and local — restore the
property and fill it by joining `AirlineId` in both queries. Note that `LoyaltyProgramConfiguration`
maps the airline with `HasOne<Airline>()` and **no navigation property** — Airline is a separate
aggregate — so that join is explicit in the query, not `p.Airline.AllianceCode`.

### Import files

`System.Text.Json` ignores unmapped properties by default, so an old import file carrying a
program-level `allianceCode` keeps importing, silently without it. That is the intended behaviour:
alliance now arrives through the airline import. Worth a line in the PR description.

---

## Accepted consequences

- **Filtering and sorting the raw programs table by alliance goes away** with the column. "Which
  programs are in Star Alliance" is a question about airlines: the alliance page lists its airlines
  (#174), each of which links to its programs (#173).
- **A program with no airline has no alliance at all** — including the `ALLIANCE` kind in
  `loyalty-program-kinds` ("Multi-airline alliance program"). No such row exists in any seed. If one
  is ever needed, it belongs as a link from the alliance, not as a code on the program.
- **On existing databases, `CX-MPC` and `KE-SKY` end up unlinked.** Their airlines are absent
  today, so the migration has nowhere to copy their alliance to. The next boot seeds Cathay Pacific
  and Korean Air (part 1), but the seeder does not revisit programs that already exist, so an admin
  links those two programs from the panel — two rows, once. Inserting reference data from a
  migration was rejected: it duplicates the seeder's job and fights its id sequence.

---

## Testing

The compiler finds most of this: `Create`, `CreateSeeded` and `Update` lose a parameter. There are
44 call sites (39 of them in tests), but only **11** pass the alliance positionally — the rest rely
on the default and compile unchanged. Four further test lines assert a program-level
`AllianceCode` by name. Beyond following the compiler:

- **Domain** — `LoyaltyProgram.Create`/`Update` no longer accept an alliance (signature change,
  covered by existing creation tests).
- **Commands** — create/update handler tests assert the round trip without the field; validator
  tests drop the `MaximumLength(8)` cases.
- **Import** — an import DTO carrying a program-level `allianceCode` still succeeds, and the
  program is created without one. This is the regression test for the silently-ignored property.
- **Export** — `ExportLoyaltyProgramsQueryHandlerTests` asserts the CSV header no longer has the
  column.
- **Browse** — `BrowsableTables` no longer lists it for `loyalty-programs`, so the grid, record
  page, filter list and CSV all lose it in one place.
- **Seeder** — a fresh seed gives British Airways `OW`, and gives `CX-MPC` an airline link where it
  had none.
- **Migration** — applied against a local Postgres seeded from the current files, then asserted:
  the 21 airlines carry their alliance, a program-set alliance never overwrites an airline-set one,
  and a junk code leaves the airline null rather than failing the migration.
- **Untouched, and checked** — `LoungeEligibilityTests` must pass unchanged; the tier columns it
  reads are not in scope.

CI runs lint, build and tests on the PR (#143); `gh pr checks` before calling it ready.

---

## Sequencing

1. **This PR** (backend #504): seed, migration, removal, tests, regenerated `spec/openapi.json`.
2. **Panel issue**, opened once #504 merges: remove the alliance input from
   `LoyaltyProgramFormPage`, remove the Alliance column and `allianceCode` payload from
   `AirlineProgramsSection`, regenerate the orval client, and signal an unlinked program — an amber
   *Not linked* in place of a bare `—` in the Airline cell, and one line on the program page saying
   that with no airline there is no alliance. `columnValue`'s `AllianceCode` renderer stays as it
   is; airlines and tiers still have the column.
3. **Follow-up issue**: `LoyaltyTier.AllianceCode`, the same duplication one level down, on the one
   path that actually reads it.

The panel PR merges after the backend PR, never alongside it.
