# Verified Reference Data — Design Spec

**Date:** 2026-05-10  
**Status:** Approved

---

## Overview

Deprecate the startup seeder, add a `Verified` flag (with audit metadata) to all verifiable reference data entity types, and introduce admin API endpoints to verify records, export verified data per type as JSON, and import that JSON into other environments via upsert.

---

## Goals

1. Stop running `ReferenceDataSeeder` at startup — seeded data stays, just no new seeding.
2. Add `IsVerified`, `VerifiedAt`, `VerifiedBy` to all 8 verifiable entity types.
3. Admin API to mark/unmark individual records as verified.
4. Admin API to export verified records per type as a downloadable JSON file.
5. Admin API to import that JSON into another environment via upsert.

---

## Out of Scope

- Hiding unverified data from end users (verification is a pure data quality flag; all records are served regardless).
- Verified badge display in the client app (future work).
- Bulk verify (all records of a type at once).
- Import of verification state — imported records always arrive as unverified in the target environment. Superseded by 2026-09-14-auto-verification-design.md §6/§7.

---

## Verifiable Entity Types

8 aggregate types participate in verification. Child entities are covered by their parent aggregate — `LoyaltyTier` is verified by its `LoyaltyProgram`, `LoungeProviderLink` is verified by its `Lounge`.

| Entity | Table | Base Class |
|---|---|---|
| Airport | `airports` | `SeedableEntity` |
| Airline | `airlines` | `SeedableEntity` |
| Alliance | `alliances` | `SeedableEntity` |
| Country | `countries` | `SeedableEntity` |
| Lounge | `lounges` | `IAggregateRoot` (direct) |
| LoungeProvider | `lounge_providers` | direct |
| LoyaltyProgram | `loyalty_programs` | `IAggregateRoot` (direct) |
| LoyaltyProgramKind | `loyalty_program_kinds` | direct |

---

## Section 1 — Seeder Deprecation

**Change:** Remove `AddHostedService<ReferenceDataSeeder>()` from `Program.cs`.

- The `ReferenceDataSeeder` class, seed JSON files under `SeedData/`, and the `SeedVersion` table/entity are all **kept** — they serve as historical reference.
- No data is deleted. All previously seeded rows remain in the database with `is_verified = false` (the default) until an admin explicitly verifies them.

---

## Section 2 — Domain Layer

### `IVerifiable` interface

New file: `FlightAssistant.Domain/Common/Interfaces/IVerifiable.cs`

```csharp
public interface IVerifiable
{
    bool IsVerified { get; }
    DateTime? VerifiedAt { get; }
    string? VerifiedBy { get; }
    void Verify(string verifiedBy);
    void Unverify();
}
```

### `SeedableEntity` changes

`SeedableEntity` implements `IVerifiable`. The 3 properties and 2 methods are added to the base class. All 4 simple ref data types (Airport, Airline, Alliance, Country) inherit verification for free.

```csharp
public class SeedableEntity : ISeedableEntity, IVerifiable
{
    // existing fields ...

    public bool IsVerified { get; private set; }
    public DateTime? VerifiedAt { get; private set; }
    public string? VerifiedBy { get; private set; }

    public void Verify(string verifiedBy)
    {
        IsVerified = true;
        VerifiedAt = DateTime.UtcNow;
        VerifiedBy = verifiedBy;
    }

    public void Unverify()
    {
        IsVerified = false;
        VerifiedAt = null;
        VerifiedBy = null;
    }
}
```

### Lounge, LoungeProvider, LoyaltyProgram, LoyaltyProgramKind

Each gets `: IVerifiable` on the class declaration, the same 3 private-set properties, and the same `Verify`/`Unverify` method implementations. No shared base class — the interface is the contract.

---

## Section 3 — Database

**Single migration:** `AddVerificationColumns`

Columns added to all affected tables:

```sql
is_verified  BOOLEAN      NOT NULL DEFAULT FALSE,
verified_at  TIMESTAMPTZ  NULL,
verified_by  VARCHAR(255) NULL
```

Tables receiving the columns: `airports`, `airlines`, `alliances`, `countries`, `lounges`, `lounge_providers`, `loyalty_programs`, `loyalty_program_kinds`.

EF Core config: `IsVerified` mapped to `is_verified`, `VerifiedAt` to `verified_at`, `VerifiedBy` to `verified_by`. Configured in the existing entity type configurations in `FlightAssistant.Infrastructure`.

---

## Section 4 — Verify API

### Endpoints

All endpoints are admin-only (`"AdminOnly"` policy). `verifiedBy` is extracted from the admin's JWT identity (`oid`/`sub` claim, same as `ClaimsPrincipalExtensions.GetTravelerId()` pattern).

```
PATCH /admin/airports/{id}/verify
PATCH /admin/airlines/{id}/verify
PATCH /admin/alliances/{id}/verify
PATCH /admin/countries/{id}/verify
PATCH /admin/lounges/{id}/verify
PATCH /admin/lounge-providers/{id}/verify
PATCH /admin/loyalty-programs/{id}/verify
PATCH /admin/loyalty-program-kinds/{id}/verify
```

Request body:
```json
{ "isVerified": true }
```

Returns `204 No Content` on success, `404` if entity not found.

### Application Layer

All 8 handlers do exactly the same thing (load entity → call `Verify`/`Unverify` → save), so a single generic command is used:

```csharp
public record VerifyEntityCommand<TEntity>(int Id, bool IsVerified, string VerifiedBy)
    : IRequest<Unit> where TEntity : class, IVerifiable;
```

One handler per entity type is still required because EF `DbSet<T>` requires a concrete type at the call site, but the handler body is identical across all 8. File: `Application/Commands/Admin/Verify/VerifyEntityCommand.cs` (shared command record) + one handler file per entity type.

---

## Section 5 — Export API

### Endpoints

```
GET /admin/export/airports
GET /admin/export/airlines
GET /admin/export/alliances
GET /admin/export/countries
GET /admin/export/lounges
GET /admin/export/lounge-providers
GET /admin/export/loyalty-programs
GET /admin/export/loyalty-program-kinds
```

Returns a **JSON file download** for all records where `is_verified = true`.

Response headers:
```
Content-Type: application/json
Content-Disposition: attachment; filename="airports-export.json"
```

Verification metadata (`IsVerified`, `VerifiedAt`, `VerifiedBy`) is **not included** in the payload — export contains only the entity data fields.

### Export Shapes

**Airport / Airline / Alliance / Country** — flat JSON arrays matching their existing field set.

**LoyaltyProgram** — includes tiers inline:
```json
[{
  "code": "BAEC", "name": "...", "kindCode": "Airline",
  "airlineIata": "BA", "allianceCode": "OW",
  "tiers": [{ "code": "Bronze", "name": "...", "rank": 1, "allianceCode": null, "allianceTierCode": null }]
}]
```

**Lounge** — includes provider links inline:
```json
[{
  "name": "Galleries Club", "airportIata": "LHR", "terminal": "T3",
  "locationHint": null, "hours": null,
  "isAirside": true, "isDomesticOnly": false, "isInternationalOnly": false,
  "amenities": null,
  "providers": [{ "providerCode": "PP", "role": "Access", "minAllianceTierCode": null, "notes": null }]
}]
```

### Dependency Ordering (caller responsibility)

When setting up a new environment, import in this order to satisfy FK constraints:
1. Alliances
2. Countries
3. Airports
4. Airlines
5. LoungeProviders
6. LoyaltyProgramKinds
7. Lounges (references Airports + LoungeProviders)
8. LoyaltyPrograms (references LoyaltyProgramKinds + Airlines)

---

## Section 6 — Import API

### Endpoints

```
POST /admin/import/airports
POST /admin/import/airlines
POST /admin/import/alliances
POST /admin/import/countries
POST /admin/import/lounges
POST /admin/import/lounge-providers
POST /admin/import/loyalty-programs
POST /admin/import/loyalty-program-kinds
```

Request body: JSON array in the same shape as the export payload for that type.

### Upsert Logic

Match by natural key. Insert if not found, update all fields if found.

| Entity | Natural Key |
|---|---|
| Airport | `Iata` |
| Airline | `Iata` (fallback: `Icao` when IATA is null) |
| Alliance | `Code` |
| Country | `Code` |
| LoungeProvider | `Code` |
| Lounge | `Name + AirportIata + Terminal` |
| LoyaltyProgramKind | `Code` |
| LoyaltyProgram | `Code` |

**Nested children (Lounge → provider links, LoyaltyProgram → tiers):** existing children for the matched parent are **replaced wholesale** with the imported payload. This keeps import authoritative for data migration use cases.

**Verification state:** all imported records arrive with `is_verified = false` regardless of their state in the source environment. Verification is per-environment and done explicitly after import.

**Missing FK dependencies** (e.g., importing a Lounge when the referenced airport doesn't exist) are logged as per-record errors and skipped — the rest of the batch continues.

### Response

```json
{
  "inserted": 12,
  "updated": 3,
  "errors": [
    "Lounge 'Plaza Premium': airport 'XYZ' not found — skipped"
  ]
}
```

---

## File Layout Summary

```
FlightAssistant.Domain/
  Common/
    Interfaces/
      IVerifiable.cs                          ← new
    SeedableEntity.cs                         ← add IVerifiable impl

FlightAssistant.Application/
  Commands/
    Admin/
      Verify/
        VerifyEntityCommand.cs                ← new (shared command + 8 handlers)
      Import/
        ImportAirportsCommand.cs              ← new (×8 types)
  Queries/
    Admin/
      Export/
        ExportAirportsQuery.cs                ← new (×8 types)

FlightAssistant.Infrastructure/
  Persistance/
    Configurations/
      {Entity}Configuration.cs               ← update each (×8)
    Migrations/
      AddVerificationColumns.cs              ← new

FlightAssistant.API/
  Endpoints/
    Admin/
      AdminVerifyEndpoints.cs                ← new
      AdminExportEndpoints.cs                ← new
      AdminImportEndpoints.cs                ← new
  Program.cs                                 ← remove AddHostedService<ReferenceDataSeeder>
```
