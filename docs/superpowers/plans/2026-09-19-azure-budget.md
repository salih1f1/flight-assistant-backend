# Azure Subscription Budget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show the Azure subscription's real budget and month-to-date spend on the dashboard, beside the AeroAPI gauge.

**Architecture:** A named `HttpClient` calls the Cost Management REST API with a bearer token from the existing `DefaultAzureCredential` — not the ResourceManager SDK, so the data-source completeness test covers it. A provider maps every outcome (including refusal) onto an explicit state; a cached query serves it; the panel renders the state it is given rather than deriving one.

**Tech Stack:** .NET 10, `Azure.Identity` (already referenced), MediatR, `ICacheService`, xUnit + NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`. Panel: React 19, TypeScript, Vitest, orval.

**Spec:** `docs/superpowers/specs/2026-09-19-azure-budget-design.md`

## Global Constraints

- **Never render a refusal as `$0.00`.** A 401/403 is `NotAuthorized` — gray, naming **Cost Management Reader**. Zero spend because we were refused is a lie that looks like good news on a panel whose purpose is to warn.
- **`dataAsOfUtc` comes from the Cost Management response, never from the clock.** The data lags 8–24 hours; using fetch time overstates freshness by up to a day.
- **Never assume USD.** The billing currency is a property of the account; format whatever `currency` Azure reports.
- **Absent `AzureCost:SubscriptionId` is normal, not an error** — a local developer has no subscription and must not see a red panel.
- **`message` is sanitized** — fixed categories plus facts this code controls, never a raw exception message. Follows `VerificationSourceDownloader`'s rule.
- Register the client with `.AddDataSourceObservation(DataSourceKeys.AzureCost)` **last** on the builder, so the handler sits innermost.
- Nullable reference types enabled. XML docs explain WHY, not WHAT — house style `FlightAssistant.Application/Interfaces/Verification/IVerificationSource.cs`. Document public members.
- xUnit: `[Fact]`/`[Theory]`, `Assert.Equal(expected, actual)`.
- YAGNI: one subscription, one budget, read-only. No per-service breakdown, no forecasting, no alerts, nothing that writes to Azure.
- **Any backend PR that adds an endpoint must regenerate the spec**: `scripts/generate-openapi.sh` from the repo root, committed. `ci.yml` fails the build byte-for-byte otherwise.

---

# Phase 1 — Backend (PR 1)

### Task 1: Contracts and options

**Files:**
- Create: `FlightAssistant.Application/Usage/AzureBudgetState.cs`
- Create: `FlightAssistant.Application/Usage/AzureBudgetSnapshot.cs`
- Create: `FlightAssistant.Application/Interfaces/ExternalServices/IAzureCostProvider.cs`
- Create: `FlightAssistant.Infrastructure/ExternalServices/Azure/AzureCostOptions.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: enum `AzureBudgetState`; record `AzureBudgetSnapshot`; `IAzureCostProvider.GetBudgetAsync(CancellationToken) → Task<AzureBudgetSnapshot>`; `AzureCostOptions` with `SectionName`, `SubscriptionId`, `BudgetName`, `IsConfigured`.

- [ ] **Step 1: Create the state enum**

```csharp
namespace FlightAssistant.Application.Usage
{
    /// <summary>
    /// What the Azure cost panel can honestly say. Derived once, in the provider, so the panel
    /// renders a state rather than inferring one from nulls — the same split the data-source
    /// inventory uses.
    /// </summary>
    public enum AzureBudgetState
    {
        /// <summary>Budget and spend both read.</summary>
        Ok,

        /// <summary>Cost Management answered, but no budget is defined on the subscription.</summary>
        NoBudget,

        /// <summary>No <c>AzureCost:SubscriptionId</c>. A local developer has none; not a fault.</summary>
        NotConfigured,

        /// <summary>401/403 — the managed identity lacks Cost Management Reader.</summary>
        NotAuthorized,

        /// <summary>Anything else: timeout, 5xx, a body we cannot read.</summary>
        Unavailable,
    }
}
```

- [ ] **Step 2: Create the snapshot record**

```csharp
namespace FlightAssistant.Application.Usage
{
    /// <param name="DataAsOfUtc">When Azure's own data was last updated, taken from its response —
    /// never the time we fetched it. Cost data lags 8-24 hours, and this panel sits beside the
    /// near-live AeroAPI gauge; stamping fetch time here would overstate freshness by up to a day.
    /// Null when the response omits it, which the panel renders as "age unknown" rather than
    /// implying the figure is current.</param>
    /// <param name="Currency">Whatever Azure reports. The subscription's billing currency is a
    /// property of the account, so this is never assumed to be USD.</param>
    /// <param name="Message">Operator-facing explanation for a non-Ok state. Sanitized to fixed
    /// categories plus facts this code controls, never a raw exception message.</param>
    public sealed record AzureBudgetSnapshot(
        AzureBudgetState State,
        string? BudgetName,
        decimal? BudgetAmount,
        decimal? CurrentSpendAmount,
        string? Currency,
        DateTimeOffset? PeriodStartUtc,
        DateTimeOffset? PeriodEndUtc,
        DateTimeOffset? DataAsOfUtc,
        string? Message)
    {
        public static AzureBudgetSnapshot NotConfigured() => new(
            AzureBudgetState.NotConfigured, null, null, null, null, null, null, null,
            "AzureCost:SubscriptionId is not set.");

        public static AzureBudgetSnapshot NotAuthorized() => new(
            AzureBudgetState.NotAuthorized, null, null, null, null, null, null, null,
            "Grant the API's managed identity the Cost Management Reader role on the subscription.");

        public static AzureBudgetSnapshot Unavailable(string message) => new(
            AzureBudgetState.Unavailable, null, null, null, null, null, null, null, message);
    }
}
```

- [ ] **Step 3: Create the provider interface and options**

```csharp
using FlightAssistant.Application.Usage;

namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// Reads the subscription's budget and month-to-date spend from Azure Cost Management.
    ///
    /// <para>
    /// Never throws for an expected condition. A missing subscription id, a refusal, an outage —
    /// each maps to an <see cref="AzureBudgetState"/>, because every one of them is something the
    /// panel must be able to say out loud rather than render as an error or, worse, as zero spend.
    /// </para>
    /// </summary>
    public interface IAzureCostProvider
    {
        Task<AzureBudgetSnapshot> GetBudgetAsync(CancellationToken ct);
    }
}
```

```csharp
namespace FlightAssistant.Infrastructure.ExternalServices.Azure
{
    /// <summary>
    /// Azure Cost Management settings. Both are optional: absent <see cref="SubscriptionId"/> is
    /// the normal state for a local developer, and the panel says "not configured" rather than
    /// failing — the same shape as <c>GitHubOptions.Token</c> and <c>AeroDataBoxOptions.ApiKey</c>.
    /// </summary>
    public sealed class AzureCostOptions
    {
        // DEVIATION FROM THE SPEC, deliberate. The spec wrote these as `Azure:SubscriptionId` and
        // `Azure:BudgetName`, but appsettings.json already has an `Azure` section holding the B2C
        // sign-in policy, and mixing cost settings into it would put two unrelated concerns under
        // one key. A separate section keeps them apart, so the keys are `AzureCost:SubscriptionId`
        // and `AzureCost:BudgetName`. Anything quoting the spec's spelling — the endpoint
        // description, the NotConfigured message — must use these instead.
        public const string SectionName = "AzureCost";

        /// <summary>The subscription whose spend the panel reports.</summary>
        public string? SubscriptionId { get; set; }

        /// <summary>Which budget to use when the subscription has more than one. Unset takes the
        /// first returned — fine for one budget, and named explicitly when there are several.</summary>
        public string? BudgetName { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(SubscriptionId);
    }
}
```

- [ ] **Step 4: Build and commit**

Run: `dotnet build` — expect 0 warnings.

```bash
git add FlightAssistant.Application/Usage FlightAssistant.Application/Interfaces/ExternalServices/IAzureCostProvider.cs FlightAssistant.Infrastructure/ExternalServices/Azure
git commit -m "feat(azure): add the cost provider contract (#496)"
```

---

### Task 2: The Cost Management provider

The task where the bugs live: every branch here is a state the panel will show.

**Files:**
- Create: `FlightAssistant.Infrastructure/ExternalServices/Azure/AzureCostProvider.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/Azure/AzureCostProviderTests.cs`

**Interfaces:**
- Consumes: `IAzureCostProvider`, `AzureBudgetSnapshot`, `AzureBudgetState`, `AzureCostOptions` (Task 1).
- Produces: `AzureCostProvider`, and `AzureCostProvider.HttpClientName = "AzureCost"`.

- [ ] **Step 1: Write the failing tests**

Follow the stub-handler pattern in `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightStatusProviderTests.cs` — a fake `HttpMessageHandler` returning canned responses, and an `HttpClient` over it. Do not call Azure.

```csharp
[Fact]
public async Task NoSubscriptionId_IsNotConfigured_AndMakesNoCall()
{
    var (provider, calls) = Build(options: new AzureCostOptions(), respond: _ => Json("{}"));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(AzureBudgetState.NotConfigured, snapshot.State);
    // A local developer has no subscription. Reaching out anyway would log a confusing failure
    // for a state that is entirely expected.
    Assert.Equal(0, calls.Count);
}

[Theory]
[InlineData(HttpStatusCode.Unauthorized)]
[InlineData(HttpStatusCode.Forbidden)]
public async Task ARefusal_IsNotAuthorized_AndNamesTheRole(HttpStatusCode status)
{
    var (provider, _) = Build(respond: _ => new HttpResponseMessage(status));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(AzureBudgetState.NotAuthorized, snapshot.State);
    Assert.Contains("Cost Management Reader", snapshot.Message);
}

// The constraint this whole feature rests on: being refused must never look like spending nothing.
[Fact]
public async Task ARefusal_ReportsNoSpendFigureAtAll()
{
    var (provider, _) = Build(respond: _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Null(snapshot.CurrentSpendAmount);
    Assert.Null(snapshot.BudgetAmount);
}

[Fact]
public async Task ABudget_IsRead_WithItsAmountSpendAndPeriod()
{
    var (provider, _) = Build(respond: _ => Json("""
        { "value": [ { "name": "monthly",
            "properties": {
              "amount": 50.0,
              "timePeriod": { "startDate": "2026-09-01T00:00:00Z", "endDate": "2026-09-30T23:59:59Z" },
              "currentSpend": { "amount": 12.34, "unit": "USD" } } } ] }
        """));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(AzureBudgetState.Ok, snapshot.State);
    Assert.Equal("monthly", snapshot.BudgetName);
    Assert.Equal(50.0m, snapshot.BudgetAmount);
    Assert.Equal(12.34m, snapshot.CurrentSpendAmount);
    Assert.Equal("USD", snapshot.Currency);
}

// The billing currency is a property of the account. Formatting EUR as dollars would misstate the
// figure by a meaningful factor.
[Fact]
public async Task ANonUsdBudget_KeepsItsOwnCurrency()
{
    var (provider, _) = Build(respond: _ => Json("""
        { "value": [ { "name": "monthly", "properties": {
            "amount": 40.0, "currentSpend": { "amount": 9.5, "unit": "EUR" } } } ] }
        """));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal("EUR", snapshot.Currency);
}

[Fact]
public async Task NoBudgetDefined_IsNoBudget_NotUnavailable()
{
    var (provider, _) = Build(respond: _ => Json("""{ "value": [] }"""));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(AzureBudgetState.NoBudget, snapshot.State);
}

// The cost query is the only reason a second call exists: with no budget there is no currentSpend
// to read, and "no budget defined" on its own tells an operator nothing about spend.
[Fact]
public async Task NoBudgetDefined_StillReportsSpend_FromTheCostQuery()
{
    var (provider, _) = Build(respond: req =>
        req.RequestUri!.AbsolutePath.Contains("budgets")
            ? Json(EmptyBudgets)
            : Json(CostQueryWith(12.34m, "USD")));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(AzureBudgetState.NoBudget, snapshot.State);
    Assert.Equal(12.34m, snapshot.CurrentSpendAmount);
    Assert.Equal("USD", snapshot.Currency);
}

// With a budget in hand its own currentSpend is authoritative, so a second call is wasted quota
// against an endpoint Microsoft documents as low-throughput.
[Fact]
public async Task ABudgetWithSpend_DoesNotAlsoRunTheCostQuery()
{
    var (provider, calls) = Build(respond: _ => Json(BudgetWith(50.0m, 12.34m, "USD")));

    await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(1, calls.Count);
}

[Fact]
public async Task ANamedBudget_IsPreferredOverTheFirst()
{
    var options = new AzureCostOptions { SubscriptionId = "sub-1", BudgetName = "production" };
    var (provider, _) = Build(options, respond: _ => Json("""
        { "value": [
            { "name": "dev", "properties": { "amount": 5.0, "currentSpend": { "amount": 1.0, "unit": "USD" } } },
            { "name": "production", "properties": { "amount": 500.0, "currentSpend": { "amount": 99.0, "unit": "USD" } } } ] }
        """));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal("production", snapshot.BudgetName);
    Assert.Equal(500.0m, snapshot.BudgetAmount);
}

[Theory]
[InlineData(HttpStatusCode.InternalServerError)]
[InlineData(HttpStatusCode.TooManyRequests)]
public async Task AnUpstreamFailure_IsUnavailable(HttpStatusCode status)
{
    var (provider, _) = Build(respond: _ => new HttpResponseMessage(status));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(AzureBudgetState.Unavailable, snapshot.State);
}

[Fact]
public async Task ABodyWeCannotRead_IsUnavailable_NotACrash()
{
    var (provider, _) = Build(respond: _ => Json("not json at all"));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(AzureBudgetState.Unavailable, snapshot.State);
}

// An upstream message can name an internal address or quote raw body content; admins read this
// field in every environment. Same rule VerificationSourceDownloader sets.
[Fact]
public async Task AnUpstreamMessage_NeverReachesTheOperatorFacingText()
{
    var (provider, _) = Build(respond: _ => throw new HttpRequestException("connect failed to 10.0.0.7:443"));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(AzureBudgetState.Unavailable, snapshot.State);
    Assert.DoesNotContain("10.0.0.7", snapshot.Message ?? "");
}

// The data lags 8-24 hours. Stamping fetch time would claim a freshness we do not have.
[Fact]
public async Task DataAsOfUtc_ComesFromTheResponse_NotTheClock()
{
    var reported = new DateTimeOffset(2026, 9, 18, 6, 0, 0, TimeSpan.Zero);
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
    var (provider, _) = Build(timeProvider: clock, respond: _ => Json($$"""
        { "value": [ { "name": "monthly", "properties": {
            "amount": 50.0,
            "currentSpend": { "amount": 12.34, "unit": "USD" },
            "forecastSpend": null,
            "eTag": "x" } } ],
          "dataAsOf": "{{reported:O}}" }
        """));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Equal(reported, snapshot.DataAsOfUtc);
    Assert.NotEqual(clock.GetUtcNow(), snapshot.DataAsOfUtc);
}

[Fact]
public async Task AMissingAsOfTimestamp_IsNull_NotNow()
{
    var (provider, _) = Build(respond: _ => Json("""
        { "value": [ { "name": "monthly", "properties": {
            "amount": 50.0, "currentSpend": { "amount": 12.34, "unit": "USD" } } } ] }
        """));

    var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

    Assert.Null(snapshot.DataAsOfUtc);
}
```

Write these as in-file helpers: `Build(...)` (options, a `FakeTimeProvider`, a stub handler, and a `calls` recorder), `Json(...)`, and three canned bodies — `EmptyBudgets` (`{ "value": [] }`), `BudgetWith(amount, spend, currency)`, and `CostQueryWith(cost, currency)` shaped as the Cost Management query answers: `properties.columns` naming the columns and `properties.rows` holding the values.

Canned bodies as named helpers rather than inline literals: several tests need the same shapes, and a C# raw string literal nested in a test reads badly enough that a typo in one copy would be easy to miss.

**Where `dataAsOf` actually lives:** the shape above is this plan's assumption about the Consumption Budgets response. **Verify it against Microsoft's current API documentation for `2023-05-01` before implementing**, and if the timestamp is carried elsewhere (or not at all on that endpoint), use what is really there and say so in your report. Do not invent a field to satisfy a test — change the test to match the real contract and note the change. The *rule* that matters is "from the response, never the clock", not this particular JSON path.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter AzureCostProviderTests`
Expected: FAIL — `AzureCostProvider` does not exist.

- [ ] **Step 3: Implement**

Key points the tests above pin:

- `HttpClientName = "AzureCost"`.
- Short-circuit on `!options.IsConfigured` — return `NotConfigured()` **without** making a call.
- Map `401`/`403` → `NotAuthorized()`; any other non-success → `Unavailable(...)`; `HttpRequestException`/`TaskCanceledException` → `Unavailable(...)` with a fixed category, never the exception's message.
- Empty `value` array → `NoBudget`, and ONLY then issue the cost query for a spend figure. With a budget in hand its `currentSpend` is authoritative and a second call is wasted quota.
- Pick the budget named by `BudgetName` when set, else the first.
- `DataAsOfUtc` strictly from the response.
- A `JsonException` is `Unavailable`, not a crash.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter AzureCostProviderTests`
Expected: PASS, 15 tests.

- [ ] **Step 5: Prove the refusal test bites — do this one for real**

Temporarily map `403` to `Ok` with `CurrentSpendAmount = 0`. Re-run. Expected: `ARefusal_IsNotAuthorized_AndNamesTheRole` and `ARefusal_ReportsNoSpendFigureAtAll` both fail. Restore and re-run to green.

This is the most important verification here: a refusal rendered as zero spend is the failure that would quietly tell an operator everything is fine. Report exactly what you saw.

- [ ] **Step 6: Register the client and provider**

In `DependencyInjection.AddInfrastructure`:

```csharp
services.AddOptions<AzureCostOptions>()
    .BindConfiguration(AzureCostOptions.SectionName);

services.AddHttpClient(AzureCostProvider.HttpClientName, client =>
    {
        client.BaseAddress = new Uri("https://management.azure.com/");
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    // Last on the chain, so the observing handler is innermost and sees each transport result
    // rather than the resilience pipeline's verdict after retries.
    .AddDataSourceObservation(DataSourceKeys.AzureCost);

services.AddScoped<IAzureCostProvider, AzureCostProvider>();
```

The bearer token comes from `DefaultAzureCredential` — the same credential `Program.cs` already uses for Key Vault. Acquire it per call with scope `https://management.azure.com/.default`; cache the `TokenCredential` instance, not the token (the credential caches tokens itself).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. **`DataSourceCatalogTests.EveryNamedHttpClientHasACatalogEntry` will now FAIL** — you have registered a named client with no catalog entry. That is the test doing its job; Task 3 adds the entry. Note it in your report and move on.

- [ ] **Step 8: Commit**

```bash
git add FlightAssistant.Infrastructure/ExternalServices/Azure FlightAssistant.Infrastructure.Tests/ExternalServices/Azure FlightAssistant.Infrastructure/DependencyInjection.cs
git commit -m "feat(azure): read the subscription budget from Cost Management (#496)"
```

---

### Task 3: Catalog entry and probe

**Files:**
- Modify: `FlightAssistant.Infrastructure/DataSources/DataSourceCatalog.cs`
- Modify: `FlightAssistant.Infrastructure/DataSources/Probes/HttpSourceProbes.cs`
- Test: `FlightAssistant.Infrastructure.Tests/DataSources/DataSourceCatalogTests.cs`

**Interfaces:**
- Consumes: `AzureCostProvider.HttpClientName` (Task 2).
- Produces: `DataSourceKeys.AzureCost = "azure-cost"`.

- [ ] **Step 1: Add the key and catalog entry**

```csharp
public const string AzureCost = "azure-cost";
```

```csharp
new(DataSourceKeys.AzureCost, "Azure Cost Management", DataSourceKind.Infrastructure,
    DataSourceCriticality.Optional,
    "Reads the subscription's budget and month-to-date spend. Reports only; it cannot change anything in Azure.",
    "https://portal.azure.com/#view/Microsoft_Azure_CostManagement/Menu/~/costanalysis",
    AzureCostProvider.HttpClientName, SupportsProbe: true, ProbeCostNote: null),
```

`Optional` because the application runs fine without it, and `ProbeCostNote: null` because Cost Management reads are not billed.

- [ ] **Step 2: Add the probe**

Follow the existing HTTP probes in `HttpSourceProbes.cs`. The probe issues the same budgets `GET` and reports `Ok`, `Unauthorized` or `Failed` — it proves the role is granted, which is the question an operator pressing "Check now" on this row is actually asking.

- [ ] **Step 3: Run the catalog tests**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter DataSourceCatalogTests`
Expected: PASS — including `EveryNamedHttpClientHasACatalogEntry`, which failed at the end of Task 2.

- [ ] **Step 4: Prove the completeness test still bites**

Temporarily comment out the new catalog entry and re-run. Expected: `EveryNamedHttpClientHasACatalogEntry` fails, naming `AzureCost`. Restore and re-run to green.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add FlightAssistant.Infrastructure/DataSources FlightAssistant.Infrastructure.Tests/DataSources
git commit -m "feat(azure): put Azure Cost Management in the data source inventory (#496)"
```

---

### Task 4: Cached query and endpoint

**Files:**
- Create: `FlightAssistant.Contracts/Responses/Admin/AzureBudgetDto.cs`
- Create: `FlightAssistant.Application/Queries/Admin/Usage/GetAzureBudgetQuery.cs`
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminUsageEndpoints.cs`
- Modify: `spec/openapi.json` (regenerated — see below)
- Test: `FlightAssistant.Application.Tests/Queries/Admin/Usage/GetAzureBudgetQueryTests.cs`
- Test: `FlightAssistant.API.Tests/Integration/AdminAzureUsageEndpointTests.cs`

**Interfaces:**
- Consumes: `IAzureCostProvider`, `AzureBudgetSnapshot` (Task 1).
- Produces: `AzureBudgetDto` — the exact field list the panel binds to in Phase 2.

- [ ] **Step 1: Define the DTO**

```csharp
namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="State">Derived by the API, not the caller: the panel renders a state rather
    /// than inferring one from which fields are null.</param>
    /// <param name="DataAsOfUtc">When Azure's data was last updated, per Azure. Null means the age
    /// is unknown — which the panel says, rather than implying the figure is current.</param>
    public sealed record AzureBudgetDto(
        string State,
        string? BudgetName,
        decimal? BudgetAmount,
        decimal? CurrentSpendAmount,
        string? Currency,
        DateTimeOffset? PeriodStartUtc,
        DateTimeOffset? PeriodEndUtc,
        DateTimeOffset? DataAsOfUtc,
        string? Message);
}
```

- [ ] **Step 2: Write the failing query tests**

```csharp
[Fact]
public async Task ReadsThroughToTheProvider_OnAColdCache()
{
    var provider = Substitute.For<IAzureCostProvider>();
    provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());

    await BuildHandler(provider).Handle(new GetAzureBudgetQuery(), CancellationToken.None);

    await provider.Received(1).GetBudgetAsync(Arg.Any<CancellationToken>());
}

[Fact]
public async Task ASecondCallInsideTheWindow_IsServedFromCache()
{
    var provider = Substitute.For<IAzureCostProvider>();
    provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(OkSnapshot());
    var handler = BuildHandler(provider);

    await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);
    await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);

    // Cost Management rate-limits aggressively and the data moves every few hours at best.
    await provider.Received(1).GetBudgetAsync(Arg.Any<CancellationToken>());
}

// Caching a refusal would leave "not authorized" on screen for an hour after the role was granted,
// with nothing an operator could do to refresh it.
[Fact]
public async Task ARefusalIsNotCached()
{
    var provider = Substitute.For<IAzureCostProvider>();
    provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(AzureBudgetSnapshot.NotAuthorized());
    var handler = BuildHandler(provider);

    await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);
    await handler.Handle(new GetAzureBudgetQuery(), CancellationToken.None);

    await provider.Received(2).GetBudgetAsync(Arg.Any<CancellationToken>());
}

[Fact]
public async Task TheStateReachesTheDtoAsItsName()
{
    var provider = Substitute.For<IAzureCostProvider>();
    provider.GetBudgetAsync(Arg.Any<CancellationToken>()).Returns(AzureBudgetSnapshot.NotConfigured());

    var result = await BuildHandler(provider).Handle(new GetAzureBudgetQuery(), CancellationToken.None);

    Assert.Equal(nameof(AzureBudgetState.NotConfigured), result.State);
}
```

**Note the third test.** It is a decision, not an oversight: only `Ok` and `NoBudget` are cached. An `Unavailable` or `NotAuthorized` answer is cheap to retry and expensive to serve stale — the moment after someone grants the role is exactly when a cached refusal is most annoying.

- [ ] **Step 3: Run to verify they fail, then implement**

Run: `dotnet test FlightAssistant.Application.Tests --filter GetAzureBudgetQueryTests`
Expected: FAIL.

Cache key `azure:budget`, TTL **one hour**, via `ICacheService` — the same shape `AeroApiUsageCache` uses for its ten-minute cache. Cache only `Ok` and `NoBudget`.

- [ ] **Step 4: Map the endpoint**

On the existing `usage` group in `AdminUsageEndpoints`:

```csharp
// GET /admin/usage/azure
group.MapGet("azure", async (IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(new GetAzureBudgetQuery(), ct)))
.WithName("AdminGetAzureBudget")
.WithSummary("Azure subscription budget and month-to-date spend")
.WithDescription(
    "The subscription's budget as defined in Azure Cost Management, with spend against it. " +
    "'state' says what the API could actually read: Ok, NoBudget when none is defined, " +
    "NotConfigured when no subscription is set, NotAuthorized when the managed identity lacks " +
    "the Cost Management Reader role, or Unavailable. Azure reports cost on a delay of roughly " +
    "8-24 hours; 'dataAsOfUtc' is Azure's own timestamp for the figures, not the time of this " +
    "request. Amounts are in 'currency', which is the subscription's billing currency and not " +
    "necessarily USD. Cached for an hour, except refusals and outages, which are not cached.")
.Produces<AzureBudgetDto>(200);
```

Always `200`: every failure is a state in the body, because the panel must render "not authorized" as information rather than as a failed request.

- [ ] **Step 5: Write the failing integration test**

Follow `AdminUsageEndpointTests`. Assert: 200 with a `state` for an unconfigured test host (`NotConfigured`, since the test configuration sets no subscription); an unauthenticated caller gets 401.

- [ ] **Step 6: Regenerate the OpenAPI spec**

```bash
scripts/generate-openapi.sh
```

Run from the repo root. **This is not optional** — `ci.yml` compares the committed spec byte-for-byte and fails the build otherwise, and the admin panel's `npm run api:sync` copies this file, so Phase 2 cannot generate a client without it. Confirm `/admin/usage/azure` and `AzureBudgetDto` appear, and that no dummy credential values leaked into the output.

- [ ] **Step 7: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add FlightAssistant.Contracts FlightAssistant.Application FlightAssistant.API FlightAssistant.Application.Tests FlightAssistant.API.Tests spec/openapi.json
git commit -m "feat(azure): serve the subscription budget over the admin API (#496)"
```

- [ ] **Step 8: Open the PR and CHECK ITS CI**

```bash
git push -u origin feature/<n>-azure-budget
gh pr create --base development --title "feat(azure): show the subscription budget and spend (#496)"
gh pr checks <pr-number>
```

Wait for the checks and report what they said. A PR reported as ready on local results alone is how `development` went red on #489 — local green is a weaker claim than CI green.

---

# Phase 2 — Admin panel (PR 2)

**Do not start until Phase 1 has merged** and the spec is on `development`. Phase 2 runs in the **`flight-assistant-admin-panel` repository**; open an issue there and substitute its number for `#<panel issue>`. That is the only deliberately unfilled value in this plan.

### Task 5: The Azure spend section

**Files:**
- Modify: `openapi/openapi.json` (via `npm run api:sync && npm run api:generate`)
- Create: `src/pages/dashboard/AzureSpendSection.tsx`
- Modify: `src/pages/Dashboard.tsx`
- Test: `src/pages/dashboard/AzureSpendSection.test.tsx`

- [ ] **Step 1: Regenerate the client**

```bash
npm run api:sync && npm run api:generate
```

Confirm `src/api/generated/model/azureBudgetDto.ts` appears.

- [ ] **Step 2: Write the failing tests**

```tsx
it('shows spend against the budget', async () => {
  renderWith(budget({ budgetAmount: 50, currentSpendAmount: 12.34, currency: 'USD' }))

  expect(await screen.findByText(/\$12\.34 of \$50\.00/)).toBeInTheDocument()
})

// The billing currency is a property of the account. Formatting EUR as dollars would misstate it.
it('formats amounts in the reported currency, not dollars', async () => {
  renderWith(budget({ budgetAmount: 40, currentSpendAmount: 9.5, currency: 'EUR' }))

  expect(await screen.findByText(/€9\.50 of €40\.00/)).toBeInTheDocument()
})

// Azure lags 8-24 hours and this sits under the near-live AeroAPI gauge. Two cost figures side by
// side invite the reader to assume they mean the same kind of thing.
it('says how old the figures are', async () => {
  renderWith(budget({ dataAsOfUtc: '2026-09-18T06:00:00Z' }))

  expect(await screen.findByText(/2026-09-18 06:00 UTC/)).toBeInTheDocument()
  expect(screen.getByText(/delay/i)).toBeInTheDocument()
})

it('says the age is unknown when Azure does not report one', async () => {
  renderWith(budget({ dataAsOfUtc: null }))

  expect(await screen.findByText(/unknown/i)).toBeInTheDocument()
})

// The constraint this feature rests on: being refused must never look like spending nothing.
it('names the role when not authorized, and draws no gauge', async () => {
  renderWith(budget({
    state: 'NotAuthorized', budgetAmount: null, currentSpendAmount: null,
    message: 'Grant the API\'s managed identity the Cost Management Reader role on the subscription.',
  }))

  expect(await screen.findByText(/Cost Management Reader/)).toBeInTheDocument()
  expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  expect(screen.queryByText(/\$0\.00/)).not.toBeInTheDocument()
})

it('renders an unconfigured subscription in gray, not red', async () => {
  renderWith(budget({ state: 'NotConfigured', budgetAmount: null, currentSpendAmount: null }))

  expect(await screen.findByText(/not configured/i)).toHaveClass('text-gray-500')
})

it('shows spend but no gauge when no budget is defined', async () => {
  renderWith(budget({ state: 'NoBudget', budgetAmount: null, currentSpendAmount: 12.34 }))

  expect(await screen.findByText(/no budget/i)).toBeInTheDocument()
  expect(screen.getByText(/12\.34/)).toBeInTheDocument()
  expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
})

it('reports an outage as unavailable, not as zero spend', async () => {
  renderWith(budget({ state: 'Unavailable', budgetAmount: null, currentSpendAmount: null }))

  expect(await screen.findByText(/unavailable/i)).toBeInTheDocument()
  expect(screen.queryByText(/\$0\.00/)).not.toBeInTheDocument()
})

it('names the real period rather than calling it this month', async () => {
  renderWith(budget({
    periodStartUtc: '2026-07-01T00:00:00Z', periodEndUtc: '2026-09-30T23:59:59Z',
  }))

  expect(await screen.findByText(/2026-07-01.*2026-09-30/)).toBeInTheDocument()
})
```

That last one matters: Azure budgets can be quarterly or annual, and describing a quarterly budget as "this month" would misrepresent both the figure and how much of the period is left.

- [ ] **Step 3: Run to verify they fail, implement, run to verify they pass**

Run: `npm test -- AzureSpendSection`

Reuse the AeroAPI gauge's bar treatment so the two read as a pair. Use `Intl.NumberFormat` with the reported `currency`. Every non-`Ok` state renders as one gray or amber line with **no gauge** — a bar drawn from a figure we do not have is this section's most likely failure.

- [ ] **Step 4: Add it to the dashboard**

In `Dashboard.tsx`, directly below `<AeroApiUsageSection />`.

- [ ] **Step 5: Run the full gate**

```bash
npm test && npm run lint && npx tsc --noEmit
```

- [ ] **Step 6: Look at it in a browser**

jsdom has no layout engine, so the tests cannot show that the section reads as a pair with the AeroAPI gauge above it. Render it and look — at minimum the `Ok` state and the `NotAuthorized` state, since the latter is what you will actually see until the role is granted.

- [ ] **Step 7: Commit, open the PR, and check its CI**

```bash
git add -A
git commit -m "feat(dashboard): show the Azure subscription budget and spend (#<panel issue>)"
git push -u origin feature/<n>-azure-budget
gh pr create --base main --title "Show the Azure subscription budget and spend (#<panel issue>)"
gh pr checks <pr-number>
```

---

## Verification checklist

Before marking Phase 1 done:

- [ ] `dotnet test` passes in full
- [ ] A 403 renders as `NotAuthorized` naming the role, with **no spend figure at all** — proven by mapping 403 to a zero-spend `Ok` and watching the test fail
- [ ] Removing the catalog entry fails `EveryNamedHttpClientHasACatalogEntry`, naming `AzureCost`
- [ ] `dataAsOfUtc` comes from the response — proven with a fake clock set a day later
- [ ] A non-USD currency survives to the DTO
- [ ] `spec/openapi.json` regenerated and committed, containing `/admin/usage/azure`
- [ ] **`gh pr checks` reported green** — not just a local `dotnet test`

After the role is granted (outside this plan):

- [ ] The figures match the Azure portal's Cost analysis for the same period
- [ ] `dataAsOfUtc` is plausibly a few hours to a day behind, not the current time
