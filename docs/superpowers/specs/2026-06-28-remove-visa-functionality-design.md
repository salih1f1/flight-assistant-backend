# Remove VISA Functionality — Design

**Date:** 2026-06-28
**Status:** Approved

## Goal

Fully remove the VISA / Sherpa visa-requirements feature from the codebase. The
feature was never reached by the upstream VISA API integration, and we do not
want to spend time or money testing functionality that is not currently used.
The feature will be revisited later as part of a future major version of the
API. Git history preserves the deleted code for that rebuild.

## Decision

**Full removal.** Delete all VISA/Sherpa-specific files and surgically remove
every reference from shared files. Restore from git history when the major
version work begins.

## Files Deleted Outright

- `FlightAssistant.API/Endpoints/VisaEndpoints.cs`
- `FlightAssistant.Application/Interfaces/ExternalServices/IVisaRequirementsProvider.cs`
- `FlightAssistant.Application/Queries/Visa/GetVisaRequirementsQuery.cs`
- `FlightAssistant.Application/Validators/GetVisaRequirementsQueryValidator.cs`
- `FlightAssistant.Contracts/Responses/Visa/VisaRequirementDto.cs`
- `FlightAssistant.Infrastructure/ExternalServices/Visa/SherpaVisaRequirementsProvider.cs`
- `FlightAssistant.API.Tests/Integration/VisaEndpointTests.cs`
- `FlightAssistant.Application.Tests/Queries/Visa/GetVisaRequirementsQueryHandlerTests.cs`

Empty `Visa` directories left behind by these deletions are removed.

## Files Edited (surgical removal of VISA references only)

- `FlightAssistant.API/Program.cs` — remove `app.MapVisaEndpoints();` and the
  `"visa"` rate-limit policy block.
- `FlightAssistant.Infrastructure/DependencyInjection.cs` — remove the Sherpa
  `AddHttpClient` registration, the `IVisaRequirementsProvider` binding, and the
  now-unused `using FlightAssistant.Infrastructure.ExternalServices.Visa;`.
- `FlightAssistant.API/appsettings.json` — remove the `RateLimiting:Visa` block.
- `FlightAssistant.Application.Tests/Validators/ValidatorEdgeCaseTests.cs` —
  remove the Visa query test cases and the `using FlightAssistant.Application.Queries.Visa;`.
- `FlightAssistant.API/FlightAssistant.postman_collection.json` — remove the
  Visa request(s).

### Documentation

- `CLAUDE.md` — remove the `Sherpa:ApiKey` row from the Required Configuration
  table, remove `Visa` from the bounded-contexts list, and remove
  `Sherpa (visa)` from the External APIs list.
- `README.md` — remove the visa intro bullet, remove `Visa Rules → Sherpa API`
  from Data Sources, and move `Visa & entry requirements lookup` out of the MVP
  Features list into the `🔮 Future` section (it is deferred, not abandoned).
- `ARCHITECTURE.md` — remove the visa lookup use-case and the `Visa`
  bounded-context entry.

## Out of Scope

- Airport seed data (`airports.v1.json`): the matches "Visakhapatnam" and
  "Visalia" are incidental airport names, not the VISA feature. Left untouched.
- Sherpa API keys are sourced from Key Vault / user secrets in deployed
  environments; nothing committed in `appsettings.json` exposes them beyond the
  removed config, so no further secret cleanup is needed in this repo.

## Verification

- `dotnet build --configuration Release` succeeds.
- `dotnet test` passes.
- `grep -ri "visa\|sherpa"` over source (excluding `obj/`, `bin/`, and the
  airport seed JSON) returns no matches.

## Workflow

New GitHub issue + branch `feature/{issue}-remove-visa-functionality` off
`development`; PR back into `development`.
