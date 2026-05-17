# .NET 10 LTS Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade all projects from `net8.0` to `net10.0` (the current LTS) and update all NuGet packages to their .NET 10-compatible versions.

**Architecture:** Each project's `<TargetFramework>` is updated independently. Package updates follow the .NET versioning convention: `Microsoft.*`, `EF Core`, and `Npgsql.EF` packages align to `10.0.x`; third-party packages (Serilog, FluentValidation, xUnit, etc.) are upgraded to their latest stable versions. The GitHub Actions CI workflow is updated to use the .NET 10 SDK.

**Tech Stack:** .NET 10 LTS (released November 2025), EF Core 10, Npgsql EF 10, ASP.NET Core 10.

> **Important — Swashbuckle / OpenAPI:** ASP.NET Core 9+ ships built-in OpenAPI support (`Microsoft.AspNetCore.OpenApi`). The CI workflow uses `dotnet swagger tofile` which depends on `Swashbuckle.AspNetCore`. If Swashbuckle does not publish a .NET 10-compatible version, migrate to built-in OpenAPI + update the CI export step (see Task 3 below for the decision point).

---

## File Map

| Action | File |
|--------|------|
| Modify | `FlightAssistant.API/FlightAssistant.API.csproj` |
| Modify | `FlightAssistant.Application/FlightAssistant.Application.csproj` |
| Modify | `FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj` |
| Modify | `FlightAssistant.Domain/FlightAssistant.Domain.csproj` |
| Modify | `FlightAssistant.Contracts/FlightAssistant.Contracts.csproj` |
| Modify | `FlightAssistant.SharedKernel/FlightAssistant.SharedKernel.csproj` |
| Modify | `FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj` |
| Modify | `FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj` |
| Modify | `.github/workflows/development_flight-assistant-dev-api.yml` |
| Possibly modify | `FlightAssistant.API/Program.cs` (OpenAPI wiring) |

---

## Task 1: Update TargetFramework in all projects

**Files:** All `.csproj` files listed above.

- [ ] **Step 1: Update TargetFramework in every project**

Run this sed command to replace all occurrences in one shot:

```bash
find . -name "*.csproj" -not -path "*/obj/*" -not -path "*AirlinesExtractor*" -not -path "*AirportsExtractor*" \
  -exec sed -i '' 's/<TargetFramework>net8\.0<\/TargetFramework>/<TargetFramework>net10.0<\/TargetFramework>/g' {} +
```

- [ ] **Step 2: Verify all projects now target net10.0**

```bash
grep -r "TargetFramework" --include="*.csproj" . | grep -v obj | grep -v AirportsExtractor | grep -v AirlinesExtractor
```

Expected: Every line shows `net10.0`. If any line still shows `net8.0`, update it manually.

- [ ] **Step 3: Attempt a build (will fail — packages not updated yet)**

```bash
dotnet build FlightAssistant.API/ --configuration Release 2>&1 | tail -20
```

Expected: Errors about package incompatibility or missing SDK. This is expected at this point.

- [ ] **Step 4: Commit the framework change separately**

```bash
git add --all "*.csproj"
git commit -m "chore: update all projects to net10.0"
```

---

## Task 2: Update NuGet packages

**Files:** All `.csproj` files.

Update every package to its latest stable .NET 10-compatible version. Use NuGet.org to verify versions if uncertain.

- [ ] **Step 1: Update Microsoft.EntityFrameworkCore packages in Infrastructure**

```bash
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package Microsoft.EntityFrameworkCore --version 10.0.*
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package Microsoft.EntityFrameworkCore.Design --version 10.0.*
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Microsoft.EntityFrameworkCore.Design --version 10.0.*
```

> Replace `10.0.*` with the latest 10.0.x version shown on NuGet.org (e.g., `10.0.5`).

- [ ] **Step 2: Update Npgsql EF Core provider**

```bash
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.*
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.*
```

- [ ] **Step 3: Update Microsoft.Extensions.* packages in Infrastructure**

```bash
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package Microsoft.Extensions.Caching.Memory --version 10.0.*
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package Microsoft.Extensions.Caching.StackExchangeRedis --version 10.0.*
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package Microsoft.Extensions.Http --version 10.0.*
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package Microsoft.Extensions.Diagnostics.HealthChecks --version 10.0.*
```

- [ ] **Step 4: Update Microsoft.Extensions.Http.Resilience**

```bash
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package Microsoft.Extensions.Http.Resilience
```

No version pin — let NuGet resolve the latest stable compatible with net10.

- [ ] **Step 5: Update API-layer packages**

```bash
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Microsoft.Identity.Web
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Serilog.AspNetCore
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Serilog.Enrichers.Environment
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Serilog.Sinks.ApplicationInsights
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Microsoft.ApplicationInsights.AspNetCore
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Azure.Identity
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Azure.Extensions.AspNetCore.Configuration.Secrets
```

- [ ] **Step 6: Update HealthChecks**

```bash
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package AspNetCore.HealthChecks.Redis
```

Verify the version resolves to a package compatible with net10 on NuGet.org. If the latest Xabaril health checks don't publish a net10 version, check if the net9 version runs on net10 (it usually does via netstandard2.0 or net8+ compatibility).

- [ ] **Step 7: Update test packages**

```bash
dotnet add FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj package Microsoft.NET.Test.Sdk
dotnet add FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj package xunit
dotnet add FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj package xunit.runner.visualstudio
dotnet add FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj package Microsoft.NET.Test.Sdk
dotnet add FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj package xunit
dotnet add FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj package xunit.runner.visualstudio
dotnet add FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj package NSubstitute
dotnet add FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj package coverlet.collector
dotnet add FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj package coverlet.collector
```

- [ ] **Step 8: Update dotAPNS in Infrastructure**

```bash
dotnet add FlightAssistant.Infrastructure/FlightAssistant.Infrastructure.csproj package dotAPNS
```

Check NuGet.org — if dotAPNS does not have a net10-compatible release, keep the current version (4.6.0) and verify it runs on net10 via netstandard2.0 compatibility.

- [ ] **Step 9: Commit package updates**

```bash
git add --all "*.csproj"
git commit -m "chore: update all NuGet packages for .NET 10 compatibility"
```

---

## Task 3: Resolve Swashbuckle / OpenAPI decision and fix compilation

**Files:** Potentially `FlightAssistant.API/FlightAssistant.API.csproj`, `FlightAssistant.API/Program.cs`, `.github/workflows/development_flight-assistant-dev-api.yml`.

- [ ] **Step 1: Check if Swashbuckle supports .NET 10**

```bash
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Swashbuckle.AspNetCore 2>&1
```

If the command succeeds and resolves a version that targets net10 or netstandard2.0, you can keep Swashbuckle. Move to Step 3.

If the package cannot be resolved for net10, proceed to Step 2.

- [ ] **Step 2 (only if Swashbuckle fails): Migrate to built-in OpenAPI**

Remove Swashbuckle and add the built-in package:

```bash
dotnet remove FlightAssistant.API/FlightAssistant.API.csproj package Swashbuckle.AspNetCore
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package Microsoft.AspNetCore.OpenApi
```

In `Program.cs`, replace Swashbuckle wiring:

```csharp
// Remove:
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
// ...
app.UseSwagger();
app.UseSwaggerUI();

// Add:
builder.Services.AddOpenApi();
// ...
app.MapOpenApi();
```

Update the CI workflow's "Export OpenAPI spec" step (see Task 4).

- [ ] **Step 3: Build the solution and fix compilation errors**

```bash
dotnet build --configuration Release 2>&1
```

Work through any remaining errors. Common issues after a major .NET upgrade:
- API changes in EF Core 10 (e.g., `HasColumnType` changes for timestamps).
- Nullable reference type warnings promoted to errors.
- Breaking changes in `Microsoft.Identity.Web` if they updated their namespaces.

Fix each error. Check the official EF Core 10 migration guide and `Microsoft.Identity.Web` changelogs if needed.

- [ ] **Step 4: Commit compilation fixes**

```bash
git add -p  # stage only changed .cs files, not .csproj (already committed)
git commit -m "fix: resolve compilation errors after .NET 10 upgrade"
```

---

## Task 4: Run tests and fix failures

- [ ] **Step 1: Run all tests**

```bash
dotnet test --configuration Release 2>&1
```

- [ ] **Step 2: Fix any test failures**

Common causes after a .NET upgrade:
- xUnit behavior changes (rare but possible).
- NSubstitute incompatibilities (update if needed).
- EF Core behavior changes in test-time model configuration.

For each failing test: read the failure message, fix the root cause, re-run only that test class before re-running the full suite.

- [ ] **Step 3: Verify green**

```bash
dotnet test --configuration Release --no-build -v normal 2>&1 | tail -20
```

Expected: All tests pass.

- [ ] **Step 4: Commit test fixes**

```bash
git add -p
git commit -m "fix: update tests for .NET 10 compatibility"
```

---

## Task 5: Update GitHub Actions workflow

**Files:** `.github/workflows/development_flight-assistant-dev-api.yml`

- [ ] **Step 1: Update the .NET SDK version**

In the workflow file, change:

```yaml
      - name: Set up .NET Core
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.x'
```

to:

```yaml
      - name: Set up .NET Core
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'
```

- [ ] **Step 2 (only if Swashbuckle was removed): Update the OpenAPI export step**

If you migrated to built-in OpenAPI in Task 3, replace the "Export OpenAPI spec" step with one that uses `dotnet openapi` or a compatible tool. One option using `Swashbuckle.AspNetCore.Cli` alternative: check `Microsoft.AspNetCore.OpenApi` docs for the recommended CLI export command. A simple approach is generating via `curl` against a running test server, or using `NSwag`. The exact replacement depends on tooling available at migration time — update this step accordingly.

If you kept Swashbuckle, no change needed here.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/development_flight-assistant-dev-api.yml
git commit -m "ci: update workflow to use .NET 10 SDK"
```

---

## Task 6: Verify EF migration bundle still works

- [ ] **Step 1: Install the latest dotnet-ef CLI**

```bash
dotnet tool update --global dotnet-ef
```

- [ ] **Step 2: Build the EF migration bundle locally**

```bash
dotnet ef migrations bundle \
  --project FlightAssistant.Infrastructure \
  --startup-project FlightAssistant.API \
  --configuration Release \
  --output efbundle-test
```

Expected: `Build succeeded.` and `efbundle-test` binary created.

- [ ] **Step 3: Clean up test bundle**

```bash
rm efbundle-test
```

- [ ] **Step 4: Final full build + test run**

```bash
dotnet build --configuration Release && dotnet test --configuration Release --no-build
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 5: Commit if any tool config changed**

```bash
git status
# commit only if there are changes (e.g., dotnet-tools.json updated)
git add .config/dotnet-tools.json
git commit -m "chore: update dotnet-ef tool for .NET 10"
```
