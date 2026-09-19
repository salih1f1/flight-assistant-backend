# App Service Health Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Say on the Jobs section whether the background jobs will actually run, and put the App Service in the data-source inventory.

**Architecture:** One ARM read of the hosting App Service through a named `HttpClient` with the existing `DefaultAzureCredential` — the same shape as `AzureCostProvider`, so the completeness test covers it. Every outcome maps to an explicit state; the panel renders the state it is given. No configuration: resource group and site name come from what App Service injects.

**Tech Stack:** .NET 10, `Azure.Identity` (already referenced), MediatR, `ICacheService`, xUnit + NSubstitute. Panel: React 19, TypeScript, Vitest, orval.

**Spec:** `docs/superpowers/specs/2026-09-19-app-service-health-design.md`

## Global Constraints

- **`alwaysOn: false` is NOT a failure.** The App Service row stays `Ok` and green; only the Jobs note is amber. A deliberate hosting choice must not paint a health page red — the same rule `NotConfigured` follows (#460) and an operator stop follows (#471).
- **The Jobs note never appears when the read failed.** `NotAuthorized` or `Unavailable` means we do not know whether Always On is on, and guessing either way is worse than silence.
- **No configuration.** `WEBSITE_RESOURCE_GROUP` and `WEBSITE_SITE_NAME` come from App Service; the subscription id from #523's resolver. Absent locally → `NotConfigured`, never an error.
- **`message` is sanitized** — fixed categories plus facts this code controls, never a raw exception message. Follows `VerificationSourceDownloader`'s rule.
- Register the client ending with `.AddDataSourceObservation(DataSourceKeys.AppService)` **last**, so the handler is innermost.
- Nullable enabled. XML docs explain WHY, not WHAT — house style `FlightAssistant.Application/Interfaces/Verification/IVerificationSource.cs`.
- xUnit: `[Fact]`/`[Theory]`, `Assert.Equal(expected, actual)`.
- YAGNI: five fields, one endpoint, read-only. **No metrics** — no `Http5xx`, no request counts, no `HealthCheckStatus`, no charts.
- **Any backend PR adding an endpoint regenerates the spec**: `scripts/generate-openapi.sh` from the repo root, committed. `ci.yml` fails byte-for-byte otherwise.
- **Before reporting a PR ready, run `gh pr checks` and say what it returned.** Local green is a weaker claim than CI green.

## The real ARM response

Captured from the live resource with a raw `GET` (not `az`, which may compose calls), so these
paths are verified rather than assumed:

```
GET .../Microsoft.Web/sites/flight-assistant-dev-api?api-version=2023-12-01

properties.state              = "Running"
properties.availabilityState  = "Normal"
properties.enabled            = true
properties.siteConfig.alwaysOn        = false
properties.siteConfig.numberOfWorkers = 1
```

`siteConfig` **is** returned inline by the site `GET` — no separate `/config/web` call is needed.

---

# Phase 1 — Backend (PR 1)

### Task 1: Contracts

**Files:**
- Create: `FlightAssistant.Application/Hosting/AppServiceState.cs`
- Create: `FlightAssistant.Application/Hosting/AppServiceSnapshot.cs`
- Create: `FlightAssistant.Application/Interfaces/ExternalServices/IAppServiceProvider.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: enum `AppServiceState`; record `AppServiceSnapshot`; `IAppServiceProvider.GetStatusAsync(CancellationToken) → Task<AppServiceSnapshot>`.

- [ ] **Step 1: Create the state enum**

```csharp
namespace FlightAssistant.Application.Hosting
{
    /// <summary>
    /// What the App Service panel can honestly say. Derived once, in the provider, so the panel
    /// renders a state rather than inferring one from nulls — the same split the data-source
    /// inventory uses.
    /// </summary>
    public enum AppServiceState
    {
        /// <summary>The resource was read.</summary>
        Ok,

        /// <summary>Not running on App Service — no <c>WEBSITE_SITE_NAME</c>. A developer machine,
        /// which is not a fault.</summary>
        NotConfigured,

        /// <summary>401/403 — the managed identity lacks Reader on the resource group.</summary>
        NotAuthorized,

        /// <summary>Anything else: timeout, 5xx, a body we cannot read.</summary>
        Unavailable,
    }
}
```

- [ ] **Step 2: Create the snapshot**

```csharp
namespace FlightAssistant.Application.Hosting
{
    /// <param name="SiteState">Azure's own word — "Running", "Stopped". Deliberately separate from
    /// <see cref="AppServiceSnapshot.State"/>, which says whether we could READ the resource: a
    /// stopped site read successfully is State=Ok, SiteState=Stopped, and conflating the two would
    /// make "we could not ask" indistinguishable from "it is not running".</param>
    /// <param name="AlwaysOn">Whether App Service keeps the app loaded. When false the app is
    /// unloaded after roughly twenty minutes idle and IHostedService stops with it, so the
    /// background jobs pause and resume with traffic. Null when unknown — which is NOT the same as
    /// false, and the panel must not treat it as such.</param>
    public sealed record AppServiceSnapshot(
        AppServiceState State,
        string? SiteName,
        string? SiteState,
        string? AvailabilityState,
        bool? Enabled,
        bool? AlwaysOn,
        int? InstanceCount,
        string? Message)
    {
        public static AppServiceSnapshot NotConfigured() => new(
            AppServiceState.NotConfigured, null, null, null, null, null, null,
            "Not running on Azure App Service.");

        public static AppServiceSnapshot NotAuthorized() => new(
            AppServiceState.NotAuthorized, null, null, null, null, null, null,
            "Grant the API's managed identity the Reader role on its resource group.");

        public static AppServiceSnapshot Unavailable(string message) => new(
            AppServiceState.Unavailable, null, null, null, null, null, null, message);
    }
}
```

- [ ] **Step 3: Create the provider interface**

```csharp
using FlightAssistant.Application.Hosting;

namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// Reads the App Service the API is hosted in, from ARM.
    ///
    /// <para>
    /// Never throws for an expected condition. Not running on App Service, a refusal, an outage —
    /// each maps to an <see cref="AppServiceState"/>, because every one of them is something the
    /// panel must be able to say out loud rather than render as an error.
    /// </para>
    /// </summary>
    public interface IAppServiceProvider
    {
        Task<AppServiceSnapshot> GetStatusAsync(CancellationToken ct);
    }
}
```

- [ ] **Step 4: Build and commit**

Run: `dotnet build` — expect 0 errors.

```bash
git add FlightAssistant.Application/Hosting FlightAssistant.Application/Interfaces/ExternalServices/IAppServiceProvider.cs
git commit -m "feat(hosting): add the App Service status contract (#527)"
```

---

### Task 2: The ARM provider

**Files:**
- Create: `FlightAssistant.Infrastructure/ExternalServices/Azure/AppServiceProvider.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/Azure/AppServiceProviderTests.cs`

**Interfaces:**
- Consumes: Task 1's contracts.
- Produces: `AppServiceProvider`, `AppServiceProvider.HttpClientName = "AppService"`.

- [ ] **Step 1: Write the failing tests**

Follow the stub-`HttpMessageHandler` pattern in `AzureCostProviderTests`. Never call Azure.

```csharp
// The real response, captured from the live resource with a raw ARM GET. siteConfig IS returned
// inline by the site GET — no separate /config/web call is needed.
private static string RealSiteResponse(bool alwaysOn = false) => $$"""
    { "id": "/subscriptions/x/resourceGroups/flight-assistant-dev/providers/Microsoft.Web/sites/flight-assistant-dev-api",
      "name": "flight-assistant-dev-api",
      "properties": {
        "state": "Running",
        "availabilityState": "Normal",
        "enabled": true,
        "siteConfig": { "alwaysOn": {{(alwaysOn ? "true" : "false")}}, "numberOfWorkers": 1 } } }
    """;

[Fact]
public async Task NotOnAppService_IsNotConfigured_AndMakesNoCall()
{
    // No WEBSITE_SITE_NAME: a developer machine. Reaching out anyway would log a confusing
    // failure for a state that is entirely expected.
    var (provider, calls) = Build(siteName: null, respond: _ => Json("{}"));

    var snapshot = await provider.GetStatusAsync(CancellationToken.None);

    Assert.Equal(AppServiceState.NotConfigured, snapshot.State);
    Assert.Equal(0, calls.Count);
}

[Fact]
public async Task TheSiteIsRead_WithItsStateAndAlwaysOn()
{
    var (provider, _) = Build(respond: _ => Json(RealSiteResponse(alwaysOn: false)));

    var snapshot = await provider.GetStatusAsync(CancellationToken.None);

    Assert.Equal(AppServiceState.Ok, snapshot.State);
    Assert.Equal("Running", snapshot.SiteState);
    Assert.Equal("Normal", snapshot.AvailabilityState);
    Assert.True(snapshot.Enabled);
    Assert.False(snapshot.AlwaysOn);
    Assert.Equal(1, snapshot.InstanceCount);
}

// The constraint the whole feature rests on: a deliberate hosting choice is not a fault.
[Fact]
public async Task AlwaysOnFalse_IsStillOk_NotAFailure()
{
    var (provider, _) = Build(respond: _ => Json(RealSiteResponse(alwaysOn: false)));

    var snapshot = await provider.GetStatusAsync(CancellationToken.None);

    Assert.Equal(AppServiceState.Ok, snapshot.State);
}

[Fact]
public async Task AlwaysOnTrue_IsRead_AsTrue()
{
    var (provider, _) = Build(respond: _ => Json(RealSiteResponse(alwaysOn: true)));

    Assert.True((await provider.GetStatusAsync(CancellationToken.None)).AlwaysOn);
}

// Unknown is not false. The panel decides differently on each, so they must not collapse.
[Fact]
public async Task AResponseWithNoSiteConfig_LeavesAlwaysOnNull()
{
    var (provider, _) = Build(respond: _ => Json("""
        { "name": "x", "properties": { "state": "Running", "enabled": true } }
        """));

    var snapshot = await provider.GetStatusAsync(CancellationToken.None);

    Assert.Equal(AppServiceState.Ok, snapshot.State);
    Assert.Null(snapshot.AlwaysOn);
}

[Theory]
[InlineData(HttpStatusCode.Unauthorized)]
[InlineData(HttpStatusCode.Forbidden)]
public async Task ARefusal_IsNotAuthorized_AndNamesTheRole(HttpStatusCode status)
{
    var (provider, _) = Build(respond: _ => new HttpResponseMessage(status));

    var snapshot = await provider.GetStatusAsync(CancellationToken.None);

    Assert.Equal(AppServiceState.NotAuthorized, snapshot.State);
    Assert.Contains("Reader", snapshot.Message);
}

// A refusal must not look like a site that is switched off, nor like Always On being absent.
[Fact]
public async Task ARefusal_ReportsNoFactsAtAll()
{
    var (provider, _) = Build(respond: _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

    var snapshot = await provider.GetStatusAsync(CancellationToken.None);

    Assert.Null(snapshot.AlwaysOn);
    Assert.Null(snapshot.SiteState);
    Assert.Null(snapshot.Enabled);
}

[Theory]
[InlineData(HttpStatusCode.InternalServerError)]
[InlineData(HttpStatusCode.TooManyRequests)]
public async Task AnUpstreamFailure_IsUnavailable(HttpStatusCode status)
{
    var (provider, _) = Build(respond: _ => new HttpResponseMessage(status));

    Assert.Equal(AppServiceState.Unavailable, (await provider.GetStatusAsync(CancellationToken.None)).State);
}

[Fact]
public async Task ABodyWeCannotRead_IsUnavailable_NotACrash()
{
    var (provider, _) = Build(respond: _ => Json("not json at all"));

    Assert.Equal(AppServiceState.Unavailable, (await provider.GetStatusAsync(CancellationToken.None)).State);
}

// An upstream message can name an internal address; admins read this field in every environment.
[Fact]
public async Task AnUpstreamMessage_NeverReachesTheOperatorFacingText()
{
    var (provider, _) = Build(respond: _ => throw new HttpRequestException("connect failed to 10.0.0.7:443"));

    var snapshot = await provider.GetStatusAsync(CancellationToken.None);

    Assert.Equal(AppServiceState.Unavailable, snapshot.State);
    Assert.DoesNotContain("10.0.0.7", snapshot.Message ?? "");
}

// A stopped site was read successfully. State says whether we could ASK; SiteState says what the
// answer was. Collapsing them would make "we could not reach ARM" look like "the site is down".
[Fact]
public async Task AStoppedSite_IsOkWithSiteStateStopped()
{
    var (provider, _) = Build(respond: _ => Json("""
        { "name": "x", "properties": { "state": "Stopped", "enabled": true,
          "siteConfig": { "alwaysOn": false, "numberOfWorkers": 1 } } }
        """));

    var snapshot = await provider.GetStatusAsync(CancellationToken.None);

    Assert.Equal(AppServiceState.Ok, snapshot.State);
    Assert.Equal("Stopped", snapshot.SiteState);
}
```

Write `Build(siteName, respond)`, `Json(...)` and a `calls` recorder as in-file helpers. `Build` defaults `siteName` to `"flight-assistant-dev-api"` and supplies a resource group and subscription id; it must let a test pass `null` for the site name.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter AppServiceProviderTests`
Expected: FAIL — `AppServiceProvider` does not exist.

- [ ] **Step 3: Implement**

Key points the tests pin:

- `HttpClientName = "AppService"`, base address `https://management.azure.com/`.
- Read `WEBSITE_SITE_NAME` and `WEBSITE_RESOURCE_GROUP` from `IConfiguration`, and the subscription from the same source `AzureCostOptions.SubscriptionId` now resolves (#523). **Any of the three missing → `NotConfigured()`, with no call.**
- `GET subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{name}?api-version=2023-12-01`, bearer token scope `https://management.azure.com/.default` from the injected `TokenCredential` singleton.
- `401`/`403` → `NotAuthorized()`; other non-success → `Unavailable(...)`; `HttpRequestException`/`TaskCanceledException`/`JsonException` → `Unavailable(...)` with a fixed category, never the exception's message.
- Every field read with `TryGetProperty`; absent → null. **`AlwaysOn` null means unknown, never false.**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter AppServiceProviderTests`
Expected: PASS, 13 tests (11 `[Fact]` plus two `[Theory]` of two cases each).

- [ ] **Step 5: Prove the refusal test bites**

Temporarily map `403` to an `Ok` with `AlwaysOn: false`. Re-run. Expected: `ARefusal_IsNotAuthorized_AndNamesTheRole` and `ARefusal_ReportsNoFactsAtAll` both fail. Restore, force a rebuild, re-run to green.

This is the one that matters: a refusal reported as `alwaysOn: false` would put a warning on the Jobs section that we have no evidence for. Report exactly what you saw.

- [ ] **Step 6: Register**

```csharp
services.AddHttpClient(AppServiceProvider.HttpClientName, client =>
    {
        client.BaseAddress = new Uri("https://management.azure.com/");
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddDataSourceObservation(DataSourceKeys.AppService);

services.AddScoped<IAppServiceProvider, AppServiceProvider>();
```

Follow the factory-lambda registration shape every other named-client provider in that file uses.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS, except `DataSourceCatalogTests.EveryNamedHttpClientHasACatalogEntry`, which will now **fail naming `AppService`** — you registered a named client with no catalog entry. That is the test doing its job; Task 3 adds the entry. Note it and move on. **If it does not fail, the completeness test has regressed — tell me, because that is a more serious finding than anything else here.**

- [ ] **Step 8: Commit**

```bash
git add FlightAssistant.Infrastructure/ExternalServices/Azure/AppServiceProvider.cs FlightAssistant.Infrastructure.Tests/ExternalServices/Azure/AppServiceProviderTests.cs FlightAssistant.Infrastructure/DependencyInjection.cs
git commit -m "feat(hosting): read the App Service's own state from ARM (#527)"
```

---

### Task 3: Catalog entry and probe

**Files:**
- Modify: `FlightAssistant.Infrastructure/DataSources/DataSourceCatalog.cs`
- Modify: `FlightAssistant.Infrastructure/DataSources/Probes/HttpSourceProbes.cs`

**Interfaces:**
- Consumes: `AppServiceProvider.HttpClientName` (Task 2).
- Produces: `DataSourceKeys.AppService = "appservice"`.

- [ ] **Step 1: Add the key and entry**

```csharp
public const string AppService = "appservice";
```

```csharp
new(DataSourceKeys.AppService, "Azure App Service", DataSourceKind.Infrastructure,
    DataSourceCriticality.Optional,
    "Reads the App Service's own state and whether Always On is enabled. Reports only; it cannot change anything in Azure.",
    "https://portal.azure.com/#@/resource/subscriptions/_/resourceGroups/flight-assistant-dev/providers/Microsoft.Web/sites/flight-assistant-dev-api",
    AppServiceProvider.HttpClientName, SupportsProbe: true, ProbeCostNote: null),
```

`Optional` because the API plainly runs without being able to read its own resource metadata.

- [ ] **Step 2: Add the probe**

Follow `AzureCostProbe` in `HttpSourceProbes.cs` — a standalone `IDataSourceProbe` delegating to `IAppServiceProvider`, **not** an `HttpDataSourceProbe` subclass. Same reason as #496: `HttpDataSourceProbe.BuildRequest()` is synchronous and called outside its own try, so async token acquisition could throw past it, escape `ProbeDataSourceCommandHandler`, skip both the status and audit writes, and 500 on the one screen an operator uses to diagnose a credential problem.

Map `Ok` → `Ok`, `NotConfigured` → `NotConfigured`, `NotAuthorized` → `Unauthorized`, else `Failed`.

- [ ] **Step 3: Run the catalog tests**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter DataSourceCatalogTests`
Expected: PASS — including the test that failed at the end of Task 2.

- [ ] **Step 4: Prove the completeness test still bites**

Comment out the new catalog entry, re-run. Expected: `EveryNamedHttpClientHasACatalogEntry` fails naming `AppService`. Restore, force a rebuild, re-run to green. Report what you saw.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add FlightAssistant.Infrastructure/DataSources
git commit -m "feat(hosting): put the App Service in the data source inventory (#527)"
```

---

### Task 4: Cached query and endpoint

**Files:**
- Create: `FlightAssistant.Contracts/Responses/Admin/AppServiceStatusDto.cs`
- Create: `FlightAssistant.Application/Queries/Admin/Health/GetAppServiceStatusQuery.cs`
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminHealthEndpoints.cs`
- Modify: `spec/openapi.json` (regenerated)
- Test: `FlightAssistant.Application.Tests/Queries/Admin/Health/GetAppServiceStatusQueryTests.cs`
- Test: `FlightAssistant.API.Tests/Integration/AdminAppServiceEndpointTests.cs`

**Interfaces:**
- Consumes: `IAppServiceProvider`, `AppServiceSnapshot` (Task 1).
- Produces: `AppServiceStatusDto` — the exact field list the panel binds to in Phase 2.

- [ ] **Step 1: Define the DTO**

```csharp
namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="State">Whether the resource could be READ. Distinct from
    /// <paramref name="SiteState"/>, which is what the answer was.</param>
    /// <param name="AlwaysOn">Null means unknown, which the panel must not treat as false: a
    /// warning drawn from a value we never read would have no evidence behind it.</param>
    public sealed record AppServiceStatusDto(
        string State,
        string? SiteName,
        string? SiteState,
        string? AvailabilityState,
        bool? Enabled,
        bool? AlwaysOn,
        int? InstanceCount,
        string? Message);
}
```

- [ ] **Step 2: Write the failing query tests**

```csharp
[Fact]
public async Task ReadsThroughToTheProvider_OnAColdCache()
{
    var provider = Substitute.For<IAppServiceProvider>();
    provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());

    await BuildHandler(provider).Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

    await provider.Received(1).GetStatusAsync(Arg.Any<CancellationToken>());
}

[Fact]
public async Task ASecondCallInsideTheWindow_IsServedFromCache()
{
    var provider = Substitute.For<IAppServiceProvider>();
    provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());
    var handler = BuildHandler(provider);

    await handler.Handle(new GetAppServiceStatusQuery(), CancellationToken.None);
    await handler.Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

    await provider.Received(1).GetStatusAsync(Arg.Any<CancellationToken>());
}

// Always On is something an operator changes and then immediately wants reflected, so a refusal
// or outage must not sit on screen for the whole window.
[Fact]
public async Task ARefusalIsNotCached()
{
    var provider = Substitute.For<IAppServiceProvider>();
    provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(AppServiceSnapshot.NotAuthorized());
    var handler = BuildHandler(provider);

    await handler.Handle(new GetAppServiceStatusQuery(), CancellationToken.None);
    await handler.Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

    await provider.Received(2).GetStatusAsync(Arg.Any<CancellationToken>());
}

[Fact]
public async Task TheStateReachesTheDtoAsItsName()
{
    var provider = Substitute.For<IAppServiceProvider>();
    provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(AppServiceSnapshot.NotConfigured());

    var result = await BuildHandler(provider).Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

    Assert.Equal(nameof(AppServiceState.NotConfigured), result.State);
}

[Fact]
public async Task EveryFieldReachesTheDto()
{
    var provider = Substitute.For<IAppServiceProvider>();
    provider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(
        new AppServiceSnapshot(AppServiceState.Ok, "site-a", "Running", "Normal", true, false, 3, null));

    var result = await BuildHandler(provider).Handle(new GetAppServiceStatusQuery(), CancellationToken.None);

    Assert.Equal("site-a", result.SiteName);
    Assert.Equal("Running", result.SiteState);
    Assert.Equal("Normal", result.AvailabilityState);
    Assert.True(result.Enabled);
    Assert.False(result.AlwaysOn);
    Assert.Equal(3, result.InstanceCount);
}
```

Cache key `hosting:app-service`, TTL **five minutes**, via `ICacheService`, the same shape `AzureBudgetCache` uses. Cache only `Ok`.

- [ ] **Step 3: Run to verify they fail, then implement**

Run: `dotnet test FlightAssistant.Application.Tests --filter GetAppServiceStatusQueryTests`
Expected: FAIL.

- [ ] **Step 4: Map the endpoint**

On the existing `/admin/health` group:

```csharp
// GET /admin/health/app-service
group.MapGet("app-service", async (IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(new GetAppServiceStatusQuery(), ct)))
.WithName("AdminGetAppServiceStatus")
.WithSummary("The App Service hosting this API")
.WithDescription(
    "Whether the API's own App Service is running, and whether Always On is enabled. With Always " +
    "On off, App Service unloads the app after roughly twenty minutes idle and the background " +
    "jobs stop with it — so they run while the panel is in use and pause afterwards. " +
    "'state' says whether the resource could be read: Ok, NotConfigured when not hosted on App " +
    "Service, NotAuthorized when the managed identity lacks Reader on the resource group, or " +
    "Unavailable. 'siteState' is Azure's own word for the site. 'alwaysOn' null means unknown, " +
    "not false. Cached for five minutes, except refusals and outages, which are not cached.")
.Produces<AppServiceStatusDto>(200);
```

Always `200`: every failure is a state in the body, because the panel must render "not authorized" as information rather than as a failed request. **Do not add `.ProducesProblem(500)`** — `AdminServerErrorDeclarationTests` allows a declared 500 only where the handler throws its own `DomainException(…, 500)`, and this one does not (see #513, where that mistake was made and reverted).

- [ ] **Step 5: Write the failing integration test**

Follow `AdminAzureUsageEndpointTests`: 200 with a `state` on the unconfigured test host (`NotConfigured`), and 401 for an unauthenticated caller.

- [ ] **Step 6: Regenerate the OpenAPI spec**

```bash
scripts/generate-openapi.sh
```

From the repo root. Confirm `/admin/health/app-service` and `AppServiceStatusDto` appear, and that no dummy credential values leaked in.

- [ ] **Step 7: Run the full suite, commit, open the PR, CHECK ITS CI**

```bash
dotnet test
git add -A && git commit -m "feat(hosting): serve the App Service status over the admin API (#527)"
git push -u origin feature/<n>-app-service-health
gh pr create --base development --title "feat(hosting): say whether the App Service will run the jobs (#527)"
gh pr checks <pr-number>
```

Wait for the checks and report what they said.

---

# Phase 2 — Admin panel (PR 2)

**Do not start until Phase 1 has merged** and the spec is on `development`. Phase 2 runs in the **`flight-assistant-admin-panel` repository**; open an issue there and substitute its number for `#<panel issue>`. That is the only deliberately unfilled value in this plan.

The inventory row needs no panel work — it comes from the catalog automatically.

### Task 5: The Always On note on the Jobs section

**Files:**
- Modify: `openapi/openapi.json` (via `npm run api:sync && npm run api:generate`)
- Modify: `src/pages/health/JobsSection.tsx`
- Test: `src/pages/health/JobsSection.test.tsx`

- [ ] **Step 1: Regenerate the client**

```bash
npm run api:sync && npm run api:generate
```

Confirm `src/api/generated/model/appServiceStatusDto.ts` appears. **If the build then fails on unrelated types, a backend change has landed that the panel has not caught up to — fix that as its own PR first**, as #184 did.

- [ ] **Step 2: Write the failing tests**

```tsx
// The point of the whole feature: this section says "Running", and with Always On off that is
// only true while someone is using the panel.
it('warns that the jobs pause when the app is unloaded', async () => {
  renderWith([job()], { state: 'Ok', alwaysOn: false })

  expect(await screen.findByText(/pause and resume with traffic/i)).toBeInTheDocument()
})

it('says nothing when Always On is enabled', async () => {
  renderWith([job()], { state: 'Ok', alwaysOn: true })
  await screen.findByText('Running')

  expect(screen.queryByText(/pause and resume/i)).not.toBeInTheDocument()
})

// A deliberate hosting choice is not a fault. Same rule NotConfigured follows on the source rows.
it('renders the warning in amber, not red', async () => {
  renderWith([job()], { state: 'Ok', alwaysOn: false })

  expect(await screen.findByText(/pause and resume with traffic/i)).toHaveClass('text-amber-600')
})

// Unknown is not false. A warning drawn from a value we never read has no evidence behind it.
it('says nothing when the App Service could not be read', async () => {
  renderWith([job()], { state: 'NotAuthorized', alwaysOn: null })
  await screen.findByText('Running')

  expect(screen.queryByText(/pause and resume/i)).not.toBeInTheDocument()
})

it('says nothing when not hosted on App Service', async () => {
  renderWith([job()], { state: 'NotConfigured', alwaysOn: null })
  await screen.findByText('Running')

  expect(screen.queryByText(/pause and resume/i)).not.toBeInTheDocument()
})

it('names the remedy', async () => {
  renderWith([job()], { state: 'Ok', alwaysOn: false })

  expect(await screen.findByText(/always on/i)).toBeInTheDocument()
})
```

Extend the existing `renderWith` to answer `GET /admin/health/app-service` as well as `/admin/jobs`.

- [ ] **Step 3: Run to verify they fail, implement, run to verify they pass**

Run: `npm test -- JobsSection`

The note renders **only** when `state === 'Ok' && alwaysOn === false`. Any other combination renders nothing — `null` means unknown.

- [ ] **Step 4: Run the full gate**

```bash
npm test && npm run lint && npx tsc --noEmit && npm run build
```

`npm run build` is not redundant with `tsc --noEmit`: it runs `tsc -b`, which covers project references the former misses, and that is how a breaking API change slipped through in #182.

- [ ] **Step 5: Look at it in a browser**

jsdom has no layout engine. Render the Jobs section with `alwaysOn: false` and confirm the note reads as a clarification beside the jobs rather than an error, and does not overpower the rows.

- [ ] **Step 6: Commit, open the PR, check its CI**

```bash
git add -A
git commit -m "feat(health): say when the background jobs pause with the app (#<panel issue>)"
git push -u origin feature/<n>-always-on-note
gh pr create --base main --title "Say when the background jobs pause with the app (#<panel issue>)"
gh pr checks <pr-number>
```

---

## Verification checklist

Before marking Phase 1 done:

- [ ] `dotnet test` passes in full
- [ ] A 403 reports `NotAuthorized` naming Reader, with **no facts at all** — proven by mapping 403 to a zero-fact `Ok` and watching two tests fail
- [ ] `alwaysOn: false` leaves the App Service row's own state `Ok`
- [ ] A response without `siteConfig` leaves `AlwaysOn` **null**, not false
- [ ] Removing the catalog entry fails `EveryNamedHttpClientHasACatalogEntry`, naming `AppService`
- [ ] `spec/openapi.json` regenerated and committed
- [ ] **`gh pr checks` reported green** — not just a local `dotnet test`

After deploy — the Reader role is already granted, so this is checkable immediately:

- [ ] `/admin/health/app-service` reports `state: Ok`, `siteState: Running`, `alwaysOn: false`
- [ ] The App Service appears in the data-source inventory as green
