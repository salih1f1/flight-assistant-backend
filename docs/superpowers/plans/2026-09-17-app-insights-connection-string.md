# Application Insights Connection String Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn on Application Insights wherever App Service provides its standard `APPLICATIONINSIGHTS_CONNECTION_STRING`, and keep the data it ships within the free allowance.

**Architecture:**
- A small resolver reads either configuration key.
- Both guards in `Program.cs` use the resolver: the Serilog sink and the SDK registration.
- The Serilog sink ships **Warning and above** only; lower levels still go to the console in `Development`.
- The SDK keeps adaptive sampling on, set explicitly.

**Tech Stack:** .NET 10, Microsoft.ApplicationInsights.AspNetCore 2.23.0, Serilog.Sinks.ApplicationInsights 5.0.0, xUnit, WebApplicationFactory.

**Spec:** issue #440. The user chose "enable, with cost guards" on 2026-09-17, and the Log Analytics workspace daily cap was set to 0.16 GB/day the same day, outside this repo.

## Global Constraints

- **No Azure changes and no live calls.** The workspace cap is already set. Tests must never send telemetry off the machine: any dummy connection string uses `IngestionEndpoint=http://127.0.0.1:9/`.
- **`ApplicationInsights:ConnectionString` still wins** when both keys are set. It is the repo's documented key.
- **Do not touch the test factory's OpenTelemetry-stripping block.** It is unrelated dead code. Mention it in the report.
- `spec/openapi.json` must show no diff.
- Only `--filter "FullyQualifiedName~…"` matches tests in this solution. `Name~` and `ClassName=` silently match nothing and still exit 0, so check the count every time.
- Diff production code against the **merge base**, not `origin/development`.
- The build stays warning-free. Commits use explicit paths. Nothing is pushed.

---

## Root cause (from #440, verified)

`Program.cs:50` (the Serilog sink) and `Program.cs:124` (`AddApplicationInsightsTelemetry()`) only run when `builder.Configuration["ApplicationInsights:ConnectionString"]` has a value.

App Service sets `APPLICATIONINSIGHTS_CONNECTION_STRING`. The environment-variable configuration provider maps only `__` to `:`, so that variable surfaces as the key `APPLICATIONINSIGHTS_CONNECTION_STRING` and never as `ApplicationInsights:ConnectionString`. A local probe confirmed it: with only the platform variable set, `cfg["ApplicationInsights:ConnectionString"]` is unset.

Result: in dev, neither the SDK nor the sink has ever been registered, and the Application Insights resource received no telemetry in 30 days.

## File Structure

- **Create** `FlightAssistant.API/Infrastructure/ApplicationInsightsConnection.cs`
- **Modify** `FlightAssistant.API/Program.cs`
- **Create** `FlightAssistant.API.Tests/Infrastructure/ApplicationInsightsConnectionTests.cs`
- **Create** `FlightAssistant.API.Tests/Integration/ApplicationInsightsWiringTests.cs`
- **Modify** `CLAUDE.md`

---

### Task 1: Resolve the connection string from either key, and ship less

- [ ] **Step 1: Resolver tests first (red)**

Create `FlightAssistant.API.Tests/Infrastructure/ApplicationInsightsConnectionTests.cs`. Match the namespace style of the neighbouring test folders. Build the configuration with `new ConfigurationBuilder().AddInMemoryCollection(...)` and cover these cases:

- only `ApplicationInsights:ConnectionString` set: returns it
- only `APPLICATIONINSIGHTS_CONNECTION_STRING` set: returns it
- both set to different values: returns the `ApplicationInsights:ConnectionString` value
- `ApplicationInsights:ConnectionString` is whitespace and the platform key is set: returns the platform key
- neither set, or both whitespace: returns `null`

Run the tests. Expected: they fail to compile because the type doesn't exist yet. That compile failure is the red.

- [ ] **Step 2: The resolver**

Create `FlightAssistant.API/Infrastructure/ApplicationInsightsConnection.cs`. Match the namespace of `TravelerContext.cs` in the same folder.

```csharp
    /// <summary>
    /// Resolves the Application Insights connection string (#440). The repo's documented key is
    /// <c>ApplicationInsights:ConnectionString</c>. App Service instead sets the environment variable
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>. The environment-variable provider maps only
    /// <c>__</c> to <c>:</c>, so that variable never appears under the documented key. Without this
    /// fallback, telemetry stayed off in every App Service environment that relied on the platform
    /// setting.
    /// </summary>
    public static class ApplicationInsightsConnection
    {
        public const string ConfigurationKey = "ApplicationInsights:ConnectionString";
        public const string PlatformKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

        /// <summary>The documented key when it has a value, otherwise the platform key, otherwise null.</summary>
        public static string? Resolve(IConfiguration configuration)
        {
            var documented = configuration[ConfigurationKey];
            if (!string.IsNullOrWhiteSpace(documented)) return documented;

            var platform = configuration[PlatformKey];
            return string.IsNullOrWhiteSpace(platform) ? null : platform;
        }
    }
```

Run Step 1's tests. Expected: all pass.

- [ ] **Step 3: Wiring test first (red)**

Create `FlightAssistant.API.Tests/Integration/ApplicationInsightsWiringTests.cs`. Follow the existing integration conventions: the `[Collection("Integration")]` attribute and the shared `FlightAssistantWebApplicationFactory`.

`factory.WithWebHostBuilder(b => b.UseSetting(...))` settings reach `builder.Configuration` before Program's top-level reads run. `FlightSearchRateLimitTests` relies on the same behaviour.

Two tests:

1. **Platform key only enables the SDK.**
   - Set `UseSetting(ApplicationInsightsConnection.PlatformKey, "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:9/")`.
   - Resolve `TelemetryClient` from the host's `Services` with `GetService`.
   - Assert it is **not null**.
   - Also assert that the resolved `TelemetryConfiguration.ConnectionString` equals the dummy value.
2. **No key leaves the SDK off.** On the plain factory (appsettings.Testing.json sets neither key), assert `GetService<TelemetryClient>()` is **null**.

Before Step 4, run the test class. Expected: test 1 **fails** and test 2 passes. If test 1 passes already, stop and report. If test 2 fails, stop and report: something else already registers the SDK.

- [ ] **Step 4: Use the resolver in both guards, and add the cost guards**

In `FlightAssistant.API/Program.cs`, inside `builder.Host.UseSerilog(...)`, replace

```csharp
    var aiConnectionString = ctx.Configuration["ApplicationInsights:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(aiConnectionString))
    {
        logCfg.WriteTo.ApplicationInsights(
            new TelemetryConfiguration { ConnectionString = aiConnectionString },
            TelemetryConverter.Traces);
    }
```

with

```csharp
    // Warning and above only (#440): Information, including EF Core's per-command logs and every
    // background cycle, stays in the console sink above. Shipping it would bill for dev noise.
    var aiConnectionString = ApplicationInsightsConnection.Resolve(ctx.Configuration);
    if (aiConnectionString is not null)
    {
        logCfg.WriteTo.ApplicationInsights(
            new TelemetryConfiguration { ConnectionString = aiConnectionString },
            TelemetryConverter.Traces,
            restrictedToMinimumLevel: LogEventLevel.Warning);
    }
```

Then, further down, replace

```csharp
var aiConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConnectionString))
{
    builder.Services.AddApplicationInsightsTelemetry();
}
```

with

```csharp
var aiConnectionString = ApplicationInsightsConnection.Resolve(builder.Configuration);
if (aiConnectionString is not null)
{
    // Adaptive sampling is the SDK default. It is stated explicitly because it is one of #440's
    // cost guards: it thins request and dependency telemetry under load instead of billing all of it.
    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        options.ConnectionString = aiConnectionString;
        options.EnableAdaptiveSampling = true;
    });
}
```

Add any missing `using` directives: `Serilog.Events` for `LogEventLevel`, and the resolver's namespace. Then run Step 3's tests. Expected: both pass.

- [ ] **Step 5: Check for a second log path into Application Insights**

`AddApplicationInsightsTelemetry()` also registers an `ILogger` provider. The app uses `UseSerilog`, and whether that replaces the Microsoft.Extensions.Logging providers decides whether logs could reach Application Insights **twice**: once through the Serilog sink, once through the SDK's provider. Determine which applies **from code and package behaviour, not assumption**. Look at the `UseSerilog` overload in use and its `writeToProviders` default.

- If providers are replaced, say so in the report with the evidence.
- If they are **not** replaced, stop and report. The fix would need a decision.

- [ ] **Step 6: CLAUDE.md**

Run `grep -n "ApplicationInsights" CLAUDE.md` and correct every statement this change makes incomplete or false. At minimum:

- The "One correlation id, everywhere" paragraph says `Activity.Current?.Id` is null "unless `ApplicationInsights:ConnectionString` is set". Name both keys.
- Add a row to **Required Configuration**: `ApplicationInsights:ConnectionString` is optional. App Service's `APPLICATIONINSIGHTS_CONNECTION_STRING` is accepted as a fallback, and the documented key wins when both are set.
- Add a short note on the cost guards, stating only what is true:
  - the Serilog sink ships Warning and above;
  - the SDK has adaptive sampling on;
  - the dev Log Analytics workspace (`DefaultWorkspace-…-WEU`) has a **0.16 GB/day ingestion cap**, set 2026-09-17 outside the repo. It applies to everything that workspace ingests.

Write no "every"/"all" sentence that isn't literally true of the code.

- [ ] **Step 7: Full check and commit**

```
dotnet test                                   # full suite; per-project totals as observed
dotnet build --configuration Release          # 0 errors, 0 warnings
bash scripts/generate-openapi.sh
git diff --stat spec/openapi.json             # must print NOTHING
```

```bash
git add FlightAssistant.API/Infrastructure/ApplicationInsightsConnection.cs \
        FlightAssistant.API/Program.cs \
        FlightAssistant.API.Tests/Infrastructure/ApplicationInsightsConnectionTests.cs \
        FlightAssistant.API.Tests/Integration/ApplicationInsightsWiringTests.cs \
        CLAUDE.md
git commit -m "fix: enable Application Insights from App Service's standard setting, with cost guards (#440)"
```

## After merge (controller, not part of the task)

Once the dev deploy is live:
1. Make one request and confirm it appears in the `requests` table within a few minutes.
2. Confirm it appears **once**, with no duplicate from the codeless agent.
3. Check the Log Analytics `Usage` table after a day, against the 0.16 GB/day cap.
