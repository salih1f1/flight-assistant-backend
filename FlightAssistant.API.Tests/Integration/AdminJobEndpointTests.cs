using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET</c>/<c>PUT /admin/jobs</c> (#471). Hangs off the shared <c>/admin</c> group, so the auth
/// cases here are really checking that it was mapped onto that group rather than standalone —
/// mirrors every other admin endpoint test class's own note on the identical pattern.
///
/// <para>
/// <c>job_switches</c> has exactly two possible rows — <c>JobNames.All</c> is a deliberately closed
/// list — and the "Integration" collection shares one InMemory database across every test in every
/// class in the collection (<c>AdminAuditEndpointTests</c> documents the same constraint for
/// <c>admin_audit_log</c>). So no test here assumes either table starts empty. The "no switch row was
/// written" test snapshots the row's full content (Enabled/ChangedUtc/ChangedBy/Reason, or its
/// absence) before and after the request rather than comparing a row count: a count is blind to an
/// UPDATE landing on a row a prior test already created in this shared database — same row, same
/// count, different content — which is exactly the shape of the bug this test exists to catch (an
/// upsert that runs before the config-ceiling check is enforced). The "exactly one audit row" test
/// embeds a fresh marker in its reason text and filters on that, rather than counting every
/// "JobSwitch" audit row any test has ever written.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminJobEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static string Marker() => Guid.NewGuid().ToString("N");

    /// <summary>WithWebHostBuilder hands back the base factory type, so the derived factory's
    /// CreateAdminClient convenience is out of reach — same token shape, built here (mirrors
    /// AdminUsageEndpointTests' identical helper).</summary>
    private static HttpClient AdminClient(WebApplicationFactory<Program> scopedFactory)
    {
        var client = scopedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", $"test-admin-{Guid.NewGuid()}");
        return client;
    }

    /// <summary>A host whose configuration overrides one key on top of appsettings.Testing.json —
    /// mirrors AdminHealthEndpointTests' WithRedisAndAppInsightsConfigured. Still points at the same
    /// named InMemory database as every other test (FlightAssistantWebApplicationFactory's own
    /// UseInMemoryDatabase call passes no separate root), which is exactly what the snapshot-based
    /// assertion below is written to tolerate.</summary>
    private WebApplicationFactory<Program> FactoryWithConfig(string key, string value) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

    /// <summary>The row's full observable content, not just its presence — a count alone cannot
    /// distinguish "nothing happened" from "an existing row was overwritten in place", and the
    /// config ceiling must forbid both.</summary>
    private readonly record struct SwitchSnapshot(bool Enabled, DateTime? ChangedUtc, string? ChangedBy, string? Reason);

    private static async Task<SwitchSnapshot?> SnapshotFlightSyncSwitchAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var row = await db.JobSwitches.AsNoTracking().FirstOrDefaultAsync(s => s.Job == JobNames.FlightSync);
        return row is null ? null : new SwitchSnapshot(row.Enabled, row.ChangedUtc, row.ChangedBy, row.Reason);
    }

    // ── Auth ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("admin/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().GetAsync("admin/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_RequiresAdmin()
    {
        var response = await factory.CreateClient().PutAsJsonAsync("admin/jobs/flight-sync", new { enabled = false });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().PutAsJsonAsync("admin/jobs/flight-sync", new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── GET ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Admin_ReturnsBothJobs()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jobs = await response.Content.ReadFromJsonAsync<List<AdminJobDto>>();
        Assert.Equal(JobNames.All.Count, jobs!.Count);
        Assert.Contains(jobs, j => j.Job == JobNames.FlightSync);
        Assert.Contains(jobs, j => j.Job == JobNames.Verification);
    }

    // ── PUT ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_SwitchesTheJobOff_AndReportsIt()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync("admin/jobs/flight-sync",
            new { enabled = false, reason = "burning AeroAPI budget" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<AdminJobDto>();
        Assert.False(job!.SwitchEnabled);
        Assert.False(job.EffectiveEnabled);
        Assert.Equal("burning AeroAPI budget", job.Reason);
    }

    // The config ceiling, enforced at the API rather than only in the UI.
    [Fact]
    public async Task Put_Returns409_WhenConfigHasTheJobOff()
    {
        using var scopedFactory = FactoryWithConfig("FlightSync:Enabled", "false");
        var client = AdminClient(scopedFactory);

        var response = await client.PutAsJsonAsync("admin/jobs/flight-sync", new { enabled = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // Content, not count: a prior test in this shared database (e.g. Put_SwitchesTheJobOff_AndReportsIt,
    // which runs earlier and legitimately creates this same row) means a buggy handler that upserts
    // before checking the ceiling would UPDATE that existing row rather than insert a new one — same
    // row count before and after, different content. Comparing the full snapshot catches that; a bare
    // count does not.
    [Fact]
    public async Task Put_Returns409_AndWritesNoSwitchRow_WhenConfigHasTheJobOff()
    {
        using var scopedFactory = FactoryWithConfig("FlightSync:Enabled", "false");
        var client = AdminClient(scopedFactory);
        var before = await SnapshotFlightSyncSwitchAsync(scopedFactory.Services);

        await client.PutAsJsonAsync("admin/jobs/flight-sync", new { enabled = true });

        var after = await SnapshotFlightSyncSwitchAsync(scopedFactory.Services);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Put_Returns404_ForAnUnknownJob()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/jobs/not-a-job", new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // One row, not two: the interceptor exclusion from Task 2 is what makes this hold. Filtered by a
    // fresh marker embedded in the reason rather than EntityType alone — see the class doc comment.
    [Fact]
    public async Task Put_WritesExactlyOneAuditRow_CarryingTheReason()
    {
        var marker = Marker();
        var reason = $"burning AeroAPI budget {marker}";

        await factory.CreateAdminClient().PutAsJsonAsync("admin/jobs/flight-sync", new { enabled = false, reason });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var rows = await db.AdminAuditLogs
            .Where(a => a.EntityType == "JobSwitch" && a.Summary.Contains(marker))
            .ToListAsync();

        Assert.Single(rows);
        Assert.Contains(reason, rows[0].Summary);
    }
}
