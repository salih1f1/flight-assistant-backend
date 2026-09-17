using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.API.HostedServices;
using FlightAssistant.API.Infrastructure;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/health</c> hangs off the shared <c>/admin</c> group, so the auth cases here are
/// really checking that it was mapped onto that group rather than standalone.
///
/// <para>
/// The base factory clears every registered <c>IHealthCheck</c> (the real DB/Redis/APNs/AeroAPI
/// checks all need infra this suite doesn't have), so most scenarios here register their own fake
/// checks via <c>WithWebHostBuilder</c> instead.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminHealthEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private sealed class FixedHealthCheck(HealthCheckResult result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    /// <summary>
    /// Swaps in a fake <c>IHostEnvironment</c> without touching the process's real
    /// <c>ASPNETCORE_ENVIRONMENT</c> (which stays "Testing" so the host's own required
    /// configuration — Postgres, Azure AD, AeroAPI — keeps resolving from
    /// appsettings.Testing.json). Only <c>AdminHealthReportBuilder</c>'s
    /// <c>env.IsProduction()</c> branch needs to see a different name.
    /// </summary>
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "FlightAssistant.API.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static readonly InvalidOperationException ApnsException = new("boom");

    private static WebApplicationFactory<Program> WithThreeFakeChecks(
        FlightAssistantWebApplicationFactory factory, string? environmentName = null)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                if (environmentName is not null)
                    services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));

                services.Configure<HealthCheckServiceOptions>(opts =>
                {
                    opts.Registrations.Add(new HealthCheckRegistration(
                        "database",
                        new FixedHealthCheck(HealthCheckResult.Healthy()),
                        failureStatus: null,
                        tags: []));
                    opts.Registrations.Add(new HealthCheckRegistration(
                        "aeroapi",
                        new FixedHealthCheck(HealthCheckResult.Degraded("Host unreachable")),
                        failureStatus: null,
                        tags: ["external"]));
                    opts.Registrations.Add(new HealthCheckRegistration(
                        "apns",
                        new FixedHealthCheck(HealthCheckResult.Unhealthy("APNs check threw", ApnsException)),
                        failureStatus: null,
                        tags: ["external"]));
                });
            }));
    }

    /// <summary>
    /// Repoints <see cref="FlightAssistantDbContext"/> at a real (Npgsql) relational provider aimed
    /// at a dead endpoint, replacing the base factory's InMemory provider — the only way to
    /// exercise "relational but unreachable", since <c>IsRelational()</c> alone answers <c>true</c>
    /// for Npgsql regardless of whether Postgres is actually reachable. Mirrors the EF-Core-assembly
    /// stripping <see cref="FlightAssistantWebApplicationFactory"/> itself does when swapping in
    /// InMemory: EF's internal service provider is keyed on every registered database provider's
    /// options, so the InMemory registration must be fully removed, not just shadowed by adding
    /// Npgsql alongside it, or EF throws "Services for database providers ... have been registered".
    /// </summary>
    private static WebApplicationFactory<Program> WithDeadRelationalDatabase(
        FlightAssistantWebApplicationFactory factory, string? environmentName = null)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                if (environmentName is not null)
                    services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));

                var efCoreAssemblyPrefixes = new[]
                {
                    "Microsoft.EntityFrameworkCore",
                    "Npgsql.EntityFrameworkCore",
                };
                var toRemove = services
                    .Where(d =>
                    {
                        if (d.ServiceType == typeof(FlightAssistantDbContext)) return true;
                        var svcAssembly = d.ServiceType.Assembly.GetName().Name;
                        if (svcAssembly is not null && efCoreAssemblyPrefixes.Any(p => svcAssembly.StartsWith(p)))
                            return true;
                        var implAssembly = d.ImplementationType?.Assembly.GetName().Name;
                        if (implAssembly is not null && efCoreAssemblyPrefixes.Any(p => implAssembly.StartsWith(p)))
                            return true;
                        return false;
                    })
                    .ToList();
                foreach (var d in toRemove)
                    services.Remove(d);

                // Port 1 on loopback: nothing listens there, so the connection is refused
                // immediately rather than hanging out the full Npgsql default timeout; the explicit
                // Timeout/Command Timeout bound the worst case regardless.
                services.AddDbContext<FlightAssistantDbContext>(opt =>
                    opt.UseNpgsql("Host=127.0.0.1;Port=1;Timeout=2;Command Timeout=2"));
            }));
    }

    /// <summary>WithWebHostBuilder hands back the base factory type, so the derived factory's
    /// CreateAdminClient convenience is out of reach — same token shape, built here.</summary>
    private static HttpClient AdminClient(WebApplicationFactory<Program> scopedFactory)
    {
        var client = scopedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", $"test-admin-{Guid.NewGuid()}");
        return client;
    }

    /// <summary>Registers a prepared <see cref="FlightSyncHeartbeat"/> as the singleton, overriding
    /// Program.cs's own registration (DI resolves the last descriptor). The integration factory removes
    /// every <c>IHostedService</c>, so the background service that would call <c>Mark*</c> never runs in
    /// this suite — this stands in for it.</summary>
    private static WebApplicationFactory<Program> WithFlightSyncHeartbeat(
        FlightAssistantWebApplicationFactory factory, FlightSyncHeartbeat heartbeat, string? environmentName = null) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                if (environmentName is not null)
                    services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));

                services.AddSingleton(heartbeat);
            }));

    /// <summary>
    /// Registers a <see cref="FlightSyncHeartbeat"/> instance that already has a failed run on it —
    /// DI resolves the last-registered descriptor for a given service type, so this overrides
    /// Program.cs's own <c>AddSingleton&lt;FlightSyncHeartbeat&gt;()</c>, the same technique
    /// <see cref="WithThreeFakeChecks"/> uses for <see cref="IHostEnvironment"/>.
    /// </summary>
    private static WebApplicationFactory<Program> WithFailedFlightSyncHeartbeat(
        FlightAssistantWebApplicationFactory factory, string? environmentName = null)
    {
        var heartbeat = new FlightSyncHeartbeat();
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunFailed(new InvalidOperationException("sync boom"), DateTime.UtcNow.AddMinutes(10));

        return WithFlightSyncHeartbeat(factory, heartbeat, environmentName);
    }

    /// <summary>The verification job's counterpart of <see cref="WithFailedFlightSyncHeartbeat"/>:
    /// overrides Program.cs's <c>VerificationJobHeartbeat</c> singleton with one whose last cycle
    /// failed.</summary>
    private static WebApplicationFactory<Program> WithFailedVerificationHeartbeat(
        FlightAssistantWebApplicationFactory factory, string? environmentName = null)
    {
        var heartbeat = new VerificationJobHeartbeat(TimeProvider.System);
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunFailed(new InvalidOperationException("verification boom"), DateTime.UtcNow.AddSeconds(30));

        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                if (environmentName is not null)
                    services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));

                services.AddSingleton(heartbeat);
            }));
    }

    // Distinctive sentinel values for the redis/App Insights configured-ness test below. Chosen to
    // be unmistakable in a failure diff and to never plausibly appear anywhere else in the response
    // body.
    private const string RedisConnectionStringSentinel =
        "sentinel-redis-host.example.invalid:6380,password=SuperSecretRedisPassword123!";
    private const string AppInsightsConnectionStringSentinel =
        "InstrumentationKey=11111111-2222-3333-4444-555555555555;IngestionEndpoint=https://sentinel-appinsights.example.invalid/";

    /// <summary>
    /// Adds real (sentinel-valued) Redis and Application Insights connection strings to the test
    /// host's configuration, via <c>ConfigureAppConfiguration</c> rather than a fake registration —
    /// <c>AdminHealthReportBuilder.BuildSystem</c> reads straight off <c>IConfiguration</c>, so
    /// nothing short of a real config value exercises the "is this actually configured" branch.
    ///
    /// <para>
    /// This deliberately does not also make <c>Program.cs</c>'s own build-time
    /// <c>hcBuilder.AddRedis(redisConnStr)</c> registration see the value: that read happens as a
    /// plain top-level statement before this factory's <c>ConfigureAppConfiguration</c> delegate is
    /// merged in (the same ordering the base factory's class comment already documents for
    /// <c>Cfg()</c>), so no live Redis health check is ever registered here — and even if it were,
    /// the base factory's <c>HealthCheckServiceOptions.Registrations.Clear()</c> means
    /// <c>CheckHealthAsync</c> never invokes it. Either way this test cannot attempt a real Redis
    /// connection; no <c>ConfigureServices</c> override to strip the health check is needed.
    /// </para>
    /// </summary>
    private static WebApplicationFactory<Program> WithRedisAndAppInsightsConfigured(
        FlightAssistantWebApplicationFactory factory)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Redis"] = RedisConnectionStringSentinel,
                    ["ApplicationInsights:ConnectionString"] = AppInsightsConnectionStringSentinel,
                })));
    }

    [Fact]
    public async Task GetHealth_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var client = factory.CreateUserClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_Admin_NoChecksRegistered_Returns200_HealthyWithEmptyChecks()
    {
        // The base factory clears every health check and never re-adds any: the "nothing
        // registered" edge case must render as Healthy with an empty list, not a 500.
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.Equal("Healthy", dto!.Status);
        Assert.Empty(dto.Checks);

        // Nothing is configured for Redis/AppInsights in appsettings.Testing.json — the booleans
        // must reflect that, not default to a stale "true".
        Assert.False(dto.System.RedisConfigured);
        Assert.False(dto.System.AppInsightsConfigured);
    }

    [Fact]
    public async Task GetHealth_Admin_InMemoryProvider_MigrationsAvailableIsFalse_AndDoesNotThrow()
    {
        // FlightAssistantWebApplicationFactory swaps in the EF Core InMemory provider, which
        // throws on GetPendingMigrationsAsync/GetAppliedMigrationsAsync. "Available: false" is a
        // distinct answer from "no migrations pending" — the request must not throw either way.
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.Migrations.Available);
        Assert.Equal(0, dto.Migrations.AppliedCount);
        Assert.Null(dto.Migrations.LastAppliedMigration);
        Assert.Empty(dto.Migrations.Pending);
        Assert.Equal("Non-relational database provider.", dto.Migrations.Error);
    }

    [Fact]
    public async Task GetHealth_DatabaseUnreachable_Returns200_WithMigrationsUnavailable_NotA500()
    {
        // Regression test for final-review finding C1: IsRelational() answers a *provider*
        // question (true for Npgsql whether or not Postgres is reachable), so it alone doesn't
        // guard against a live-but-unreachable database — GetAppliedMigrationsAsync and
        // GetPendingMigrationsAsync need a real connection and threw a raw NpgsqlException that
        // took the whole report down with it (confirmed against exactly this dead-endpoint setup
        // during review). BuildMigrationsAsync must degrade to Available: false instead, the same
        // "cannot tell" shape the non-relational guard already produces — not 500.
        using var scopedFactory = WithDeadRelationalDatabase(factory);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.Migrations.Available);
        Assert.Equal(0, dto.Migrations.AppliedCount);
        Assert.Null(dto.Migrations.LastAppliedMigration);
        Assert.Empty(dto.Migrations.Pending);
        // Testing (the ambient, non-overridden environment here) is non-Production, so the reason
        // renders.
        Assert.NotNull(dto.Migrations.Error);

        // The verification queue's count needs the same unreachable database: it degrades to
        // "cannot tell" too, rather than failing the report.
        Assert.Null(dto.Verification.PendingCount);
    }

    [Fact]
    public async Task GetHealth_DatabaseUnreachable_Production_NullsTheMigrationsError()
    {
        using var scopedFactory = WithDeadRelationalDatabase(factory, environmentName: Environments.Production);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.Migrations.Available);
        Assert.Null(dto.Migrations.Error);
    }

    [Fact]
    public async Task GetHealth_PerCheckShape_ReturnsNameStatusDescriptionTagsAndWorstStatusRollup()
    {
        using var scopedFactory = WithThreeFakeChecks(factory);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);

        // Unhealthy (apns) beats Degraded (aeroapi) beats Healthy (database).
        Assert.Equal("Unhealthy", dto!.Status);
        Assert.Equal(3, dto.Checks.Count);

        var database = Assert.Single(dto.Checks, c => c.Name == "database");
        Assert.Equal("Healthy", database.Status);
        Assert.Empty(database.Tags);

        var aeroapi = Assert.Single(dto.Checks, c => c.Name == "aeroapi");
        Assert.Equal("Degraded", aeroapi.Status);
        Assert.Equal("Host unreachable", aeroapi.Description);
        Assert.Contains("external", aeroapi.Tags);

        var apns = Assert.Single(dto.Checks, c => c.Name == "apns");
        Assert.Equal("Unhealthy", apns.Status);
        Assert.Equal("APNs check threw", apns.Description);
        Assert.Contains("external", apns.Tags);
    }

    [Fact]
    public async Task GetHealth_Development_PopulatesErrorForTheThrowingCheck()
    {
        // Note on what this test does and doesn't prove: it overrides IHostEnvironment to a
        // concretely-named "Development" for readability, but Testing (the ambient environment
        // this whole suite already runs under) is itself non-Production — so this assertion
        // passes identically with the override removed and does not by itself demonstrate that
        // the override mechanism works. GetHealth_Production_NullsTheErrorForTheThrowingCheck is
        // the one that's discriminating (Testing would fail that assertion without its override),
        // and the pair together prove both that population happens outside Production and that
        // null in Production isn't merely an absent field.
        using var scopedFactory = WithThreeFakeChecks(factory, environmentName: Environments.Development);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);

        var apns = Assert.Single(dto!.Checks, c => c.Name == "apns");
        Assert.NotNull(apns.Error);
        Assert.Contains("InvalidOperationException", apns.Error);
        Assert.Contains("boom", apns.Error);
    }

    [Fact]
    public async Task GetHealth_Production_NullsTheErrorForTheThrowingCheck()
    {
        // The security-relevant assertion in this set: exception detail must never reach a
        // Production caller, even for the check that actually threw.
        using var scopedFactory = WithThreeFakeChecks(factory, environmentName: Environments.Production);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);

        var apns = Assert.Single(dto!.Checks, c => c.Name == "apns");
        Assert.Null(apns.Error);

        // Description is author-written health-check text, not exception detail — it still
        // renders even in Production.
        Assert.Equal("APNs check threw", apns.Description);
    }

    [Fact]
    public async Task GetHealth_FlightSyncFailed_PopulatesLastErrorOutsideProduction()
    {
        // I2 (final review): flightSync.lastError is gated by the same includeDiagnostics flag as
        // checks[].error, but had no coverage at all. Mirrors GetHealth_Development_Populates.../
        // GetHealth_Production_Nulls... for the checks[] field, for this field.
        using var scopedFactory = WithFailedFlightSyncHeartbeat(factory, environmentName: Environments.Development);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);

        Assert.Equal("Failed", dto!.FlightSync.LastOutcome);
        Assert.NotNull(dto.FlightSync.LastError);
        Assert.Contains("InvalidOperationException", dto.FlightSync.LastError);
        Assert.Contains("sync boom", dto.FlightSync.LastError);
    }

    [Fact]
    public async Task GetHealth_FlightSyncFailed_Production_NullsTheLastError()
    {
        // The security-relevant half of the pair above: the sync loop's own exception detail must
        // never reach a Production caller either.
        using var scopedFactory = WithFailedFlightSyncHeartbeat(factory, environmentName: Environments.Production);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);

        Assert.Equal("Failed", dto!.FlightSync.LastOutcome);
        Assert.Null(dto.FlightSync.LastError);
    }

    [Fact]
    public async Task GetHealth_ReportsTheLastRunDuration()
    {
        var heartbeat = new FlightSyncHeartbeat();
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunSucceeded(processed: 3, failed: 0, nextRunDueUtc: DateTime.UtcNow.AddMinutes(1),
            duration: TimeSpan.FromSeconds(42));

        using var scopedFactory = WithFlightSyncHeartbeat(factory, heartbeat);
        var client = AdminClient(scopedFactory);

        var dto = await (await client.GetAsync("/admin/health")).Content.ReadFromJsonAsync<AdminHealthDto>();

        Assert.Equal(42, dto!.FlightSync.LastRunDurationSeconds);
    }

    [Fact]
    public async Task GetHealth_BeforeAnyRun_HasNoRunDuration()
    {
        var dto = await (await factory.CreateAdminClient().GetAsync("/admin/health")).Content.ReadFromJsonAsync<AdminHealthDto>();

        Assert.Null(dto!.FlightSync.LastRunDurationSeconds);
    }

    [Fact]
    public async Task GetHealth_Admin_ReportsTheVerificationSection_EnabledWithAPendingCount()
    {
        // Hosted services are stripped in this suite, so the job has never run: NeverRun, and the
        // count is read from the (InMemory) queue table rather than left out.
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Read as a raw string first (#385 D4): deserialising straight into AdminHealthDto below
        // would still pass even if StuckAbandoned/FailuresUnrecorded were renamed in
        // AdminHealthVerificationDto, since the C# round trip renames both ends together. Reading
        // the raw body and asserting on the literal camelCase JSON names is what actually pins them
        // — otherwise they're pinned only by the generated spec.
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var verification = doc.RootElement.GetProperty("verification");
        Assert.Equal(0, verification.GetProperty("stuckAbandoned").GetInt32());
        Assert.Equal(0, verification.GetProperty("failuresUnrecorded").GetInt32());

        var dto = JsonSerializer.Deserialize<AdminHealthDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dto);
        Assert.True(dto!.Verification.Enabled);
        Assert.Equal("NeverRun", dto.Verification.LastOutcome);
        Assert.Null(dto.Verification.LastRunStartedUtc);
        Assert.NotNull(dto.Verification.PendingCount);
        Assert.Equal(0, dto.Verification.StuckAbandoned);
        Assert.Equal(0, dto.Verification.FailuresUnrecorded);
    }

    /// <summary>
    /// #424: proves the endpoint actually surfaces a seeded Error completion's group through the full
    /// pipeline (repository read -> builder mapping -> JSON) -- the wiring, and that a real seeded row
    /// comes back correctly shaped as JSON. This is wiring/shape evidence only: InMemory has no real
    /// index to prove an index-only scan against, and that proof isn't a test at all -- it's the
    /// EXPLAIN (ANALYZE, BUFFERS) measurement against a seeded PostgreSQL container recorded in
    /// CLAUDE.md, next to `ix_verification_requests_error_attribution`. A read that silently failed to
    /// translate here would come back as the same null RecentFailures a genuinely unreachable database
    /// produces, which the NotNull/Single assertions below would catch, not let through unnoticed. A
    /// random entity key and a GUID-valued FailedSource keep this resilient to whatever other rows the
    /// shared InMemory database in this xUnit collection accumulates from other test classes.
    /// </summary>
    [Fact]
    public async Task GetHealth_Admin_RecentFailuresReflectsASeededErrorCompletion()
    {
        var nonce = "probe-" + Guid.NewGuid().ToString("N");
        var entityKey = Random.Shared.Next(10_000_000, 99_999_999).ToString();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            var request = VerificationRequest.Create(
                VerificationEntityType.Airport, entityKey, new[] { nonce }, VerificationTrigger.Admin, "admin-oid", 0m,
                DateTime.UtcNow.AddMinutes(-10));
            request.MarkError("boom", DateTime.UtcNow.AddMinutes(-1), nonce);
            db.VerificationRequests.Add(request);
            await db.SaveChangesAsync();
        }

        var dto = await (await factory.CreateAdminClient().GetAsync("admin/health")).Content.ReadFromJsonAsync<AdminHealthDto>();

        Assert.NotNull(dto);
        Assert.NotNull(dto!.Verification.RecentFailures);
        var group = Assert.Single(dto.Verification.RecentFailures!, g => g.FailedSource == nonce);
        Assert.Equal("Airport", group.EntityType);
        Assert.Equal(1, group.Count);
    }

    [Fact]
    public async Task GetHealth_VerificationFailed_PopulatesLastErrorOutsideProduction()
    {
        using var scopedFactory = WithFailedVerificationHeartbeat(factory, environmentName: Environments.Development);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.Equal("Failed", dto!.Verification.LastOutcome);
        Assert.Equal("InvalidOperationException: verification boom", dto.Verification.LastError);
    }

    [Fact]
    public async Task GetHealth_VerificationFailed_Production_NullsTheLastError()
    {
        using var scopedFactory = WithFailedVerificationHeartbeat(factory, environmentName: Environments.Production);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.Equal("Failed", dto!.Verification.LastOutcome);
        Assert.Null(dto.Verification.LastError);
    }

    [Fact]
    public async Task GetHealth_RedisAndAppInsightsConfigured_ReportsBooleansTrue_AndNeverLeaksTheConnectionStrings()
    {
        // I2 (final review): GetHealth_Admin_NoChecksRegistered_Returns200_HealthyWithEmptyChecks
        // only proves the booleans are false when nothing is configured — which would also pass if
        // they were hardcoded false. This is the real test: configure both, assert both flip to
        // true, and — the point of these fields being booleans in the first place — assert neither
        // sentinel value appears anywhere in the raw response body.
        using var scopedFactory = WithRedisAndAppInsightsConfigured(factory);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Read as a raw string first: this is what actually proves the connection strings can't
        // escape through the endpoint, as opposed to merely checking the two booleans on the
        // deserialised DTO (which would miss a leak into some other field entirely).
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(RedisConnectionStringSentinel, body);
        Assert.DoesNotContain(AppInsightsConnectionStringSentinel, body);

        var dto = JsonSerializer.Deserialize<AdminHealthDto>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dto);
        Assert.True(dto!.System.RedisConfigured);
        Assert.True(dto.System.AppInsightsConfigured);
    }

    /// <summary>
    /// #440 fix round 1, finding 1: only <see cref="ApplicationInsightsConnection.PlatformKey"/>
    /// (App Service's own <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>) is set here — nothing under
    /// the documented <c>ApplicationInsights:ConnectionString</c> key. This is the exact scenario
    /// #440 exists to fix: telemetry now flows in it (see <c>ApplicationInsightsWiringTests</c>),
    /// so this operator-facing boolean must agree, not still read the documented key raw.
    /// </summary>
    private static WebApplicationFactory<Program> WithAppInsightsPlatformKeyOnlyConfigured(
        FlightAssistantWebApplicationFactory factory)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ApplicationInsightsConnection.PlatformKey] = AppInsightsConnectionStringSentinel,
                })));
    }

    [Fact]
    public async Task GetHealth_AppInsightsPlatformKeyOnlyConfigured_ReportsAppInsightsConfiguredTrue()
    {
        using var scopedFactory = WithAppInsightsPlatformKeyOnlyConfigured(factory);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AppInsightsConnectionStringSentinel, body);

        var dto = JsonSerializer.Deserialize<AdminHealthDto>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dto);
        Assert.True(dto!.System.AppInsightsConfigured);
    }
}
