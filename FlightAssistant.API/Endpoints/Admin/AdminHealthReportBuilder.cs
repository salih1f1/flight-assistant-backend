using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlightAssistant.API.Endpoints.Admin
{
    /// <summary>
    /// Shapes the <c>GET /admin/health</c> response. Kept out of the minimal-API lambda so it is
    /// unit-testable directly, without a running host — see
    /// <see cref="AdminHealthEndpoints.MapAdminHealthEndpoints"/> for why this endpoint is mapped
    /// without MediatR in the first place.
    /// </summary>
    public static class AdminHealthReportBuilder
    {
        /// <summary>
        /// Runs every registered health check (no predicate — <c>aeroapi</c> and <c>apns</c>,
        /// tagged <c>external</c>, are included) and shapes the full report.
        /// </summary>
        public static async Task<AdminHealthDto> BuildAsync(
            HealthCheckService healthCheckService,
            FlightAssistantDbContext db,
            FlightSyncHeartbeat heartbeat,
            IHostEnvironment env,
            IConfiguration configuration,
            CancellationToken ct)
        {
            var report = await healthCheckService.CheckHealthAsync(ct);

            // Outside Production only — same convention as DomainException.DiagnosticDetail
            // (CLAUDE.md, "Error handling"): the operator-facing cause renders in Development and
            // Staging, never in Production.
            var includeDiagnostics = !env.IsProduction();

            var checks = report.Entries
                .Select(entry => BuildCheck(entry.Key, entry.Value, includeDiagnostics))
                .ToList();

            return new AdminHealthDto(
                report.Status.ToString(),
                (long)report.TotalDuration.TotalMilliseconds,
                checks,
                BuildSystem(env, configuration),
                await BuildMigrationsAsync(db, ct),
                BuildFlightSync(heartbeat, configuration, includeDiagnostics));
        }

        private static AdminHealthCheckDto BuildCheck(string name, HealthReportEntry entry, bool includeDiagnostics)
        {
            var error = includeDiagnostics && entry.Exception is not null
                ? $"{entry.Exception.GetType().Name}: {entry.Exception.Message}"
                : null;

            return new AdminHealthCheckDto(
                name,
                entry.Status.ToString(),
                entry.Description,
                (long)entry.Duration.TotalMilliseconds,
                entry.Tags.ToList(),
                error);
        }

        private static AdminHealthSystemDto BuildSystem(IHostEnvironment env, IConfiguration configuration)
        {
            var informationalVersion = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            var (version, buildSha) = ParseVersion(informationalVersion);

            // Same instant backs both fields so they cannot disagree.
            var startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var uptime = DateTime.UtcNow - startedUtc;

            // Booleans only — the same two lookups Program.cs already does at startup. Never echo
            // the connection string itself through a diagnostics endpoint.
            var redisConfigured = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis"));
            var appInsightsConfigured =
                !string.IsNullOrWhiteSpace(configuration["ApplicationInsights:ConnectionString"]);

            return new AdminHealthSystemDto(
                env.EnvironmentName,
                version,
                buildSha,
                RuntimeInformation.FrameworkDescription,
                startedUtc,
                uptime,
                redisConfigured,
                appInsightsConfigured);
        }

        /// <summary>
        /// Splits an <see cref="AssemblyInformationalVersionAttribute"/> value into the SDK-assigned
        /// version and the appended <c>SourceRevisionId</c>. The revision id is present only when
        /// the CI publish step stamped <c>-p:SourceRevisionId=&lt;sha&gt;</c> (the
        /// <c>development_flight-assistant-dev-api.yml</c> workflow); every local build's
        /// informational version has no <c>'+'</c> at all, so <c>BuildSha</c> is <c>null</c> rather
        /// than a placeholder like <c>"unknown"</c> or <c>"local"</c> that could be mistaken for a
        /// real commit. Public and static so this parsing is unit-testable without a host.
        /// </summary>
        public static (string Version, string? BuildSha) ParseVersion(string? informationalVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion))
                return ("0.0.0", null);

            var plusIndex = informationalVersion.IndexOf('+');
            if (plusIndex < 0)
                return (informationalVersion, null);

            var version = informationalVersion[..plusIndex];
            var revisionId = informationalVersion[(plusIndex + 1)..];

            if (string.IsNullOrEmpty(revisionId))
                return (version, null);

            var buildSha = revisionId.Length > 7 ? revisionId[..7] : revisionId;
            return (version, buildSha);
        }

        /// <summary>
        /// <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetPendingMigrationsAsync"/>
        /// and its <c>GetApplied</c> counterpart both throw on the InMemory provider —
        /// <see cref="FlightAssistantWebApplicationFactory"/> uses InMemory, so an unguarded call
        /// would turn this endpoint into a 500 in every integration test and bury the real failure
        /// behind it. <see cref="DbContext.Database"/>'s <c>IsRelational()</c> guards both calls; on
        /// a non-relational provider this reports "cannot tell" (<c>Available: false</c>) rather than
        /// faking "no migrations pending", which is a different, stronger claim.
        /// </summary>
        private static async Task<AdminHealthMigrationsDto> BuildMigrationsAsync(
            FlightAssistantDbContext db, CancellationToken ct)
        {
            if (!db.Database.IsRelational())
                return new AdminHealthMigrationsDto(false, 0, null, []);

            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

            return new AdminHealthMigrationsDto(
                true,
                applied.Count,
                applied.Count > 0 ? applied[^1] : null,
                pending);
        }

        private static AdminHealthFlightSyncDto BuildFlightSync(
            FlightSyncHeartbeat heartbeat, IConfiguration configuration, bool includeDiagnostics)
        {
            // Read once — never re-read the heartbeat per field, or a concurrent writer could leave
            // this response straddling two runs (e.g. a fresh Success outcome beside the previous
            // run's error).
            var snapshot = heartbeat.Current;

            // Same config key and default FlightStatusSyncBackgroundService itself reads.
            var enabled = configuration.GetValue("FlightSync:Enabled", true);

            return new AdminHealthFlightSyncDto(
                enabled,
                snapshot.LastRunStartedUtc,
                snapshot.LastRunCompletedUtc,
                snapshot.LastOutcome.ToString(),
                snapshot.FlightsProcessed,
                snapshot.FlightsFailed,
                snapshot.NextRunDueUtc,
                includeDiagnostics ? snapshot.LastError : null);
        }
    }
}
