using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using FlightAssistant.API.HostedServices;
using FlightAssistant.API.Infrastructure;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

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
        // Process.StartTime cannot change for the life of the process. Computing it once here
        // (final review, M4) avoids allocating — and never disposing — a new Process handle on
        // every request, and makes StartedUtc byte-identical across requests instead of drifting
        // by however long the health checks themselves took.
        private static readonly DateTime ProcessStartedUtc = GetProcessStartedUtc();

        private static DateTime GetProcessStartedUtc()
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }

        /// <summary>
        /// Runs every registered health check (no predicate — <c>apns</c>,
        /// tagged <c>external</c>, are included) and shapes the full report.
        /// </summary>
        public static async Task<AdminHealthDto> BuildAsync(
            HealthCheckService healthCheckService,
            FlightAssistantDbContext db,
            FlightSyncHeartbeat heartbeat,
            VerificationJobHeartbeat verificationHeartbeat,
            IOptions<VerificationOptions> verificationOptions,
            IVerificationRequestRepository verificationRequests,
            IHostEnvironment env,
            IConfiguration configuration,
            ILogger logger,
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
                await BuildMigrationsAsync(db, includeDiagnostics, logger, ct),
                BuildFlightSync(heartbeat, configuration, includeDiagnostics),
                await BuildVerificationAsync(
                    verificationHeartbeat, verificationOptions.Value.Enabled, verificationOptions.Value.StaleAfterDays,
                    verificationRequests, includeDiagnostics, logger, ct));
        }

        private static AdminHealthCheckDto BuildCheck(string name, HealthReportEntry entry, bool includeDiagnostics)
        {
            var error = includeDiagnostics && entry.Exception is not null
                ? $"{entry.Exception.GetType().Name}: {entry.Exception.Message}"
                : null;

            // entry.Description renders in every environment, unlike Error above — deliberate: it
            // is author-written health-check text, not exception detail, so it doesn't need
            // Production gating. Verified safe for all four checks registered today
            // (DatabaseHealthCheck/AeroApiHealthCheck return static or status-code-only strings,
            // ApnsHealthCheck names only missing config *keys* — never values — and
            // AspNetCore.HealthChecks.Redis sets no description at all). A future third-party check
            // inherits this always-renders rule; check what it puts in Description before adding it.
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
            var uptime = DateTime.UtcNow - ProcessStartedUtc;

            // Booleans only. Never echo the connection string itself through a diagnostics
            // endpoint. Redis is the same lookup Program.cs does at startup; App Insights uses
            // ApplicationInsightsConnection.Resolve, the same resolver both Program.cs startup
            // guards use (#440), so this agrees with them on the platform-key fallback too.
            var redisConfigured = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis"));
            var appInsightsConfigured = ApplicationInsightsConnection.Resolve(configuration) is not null;

            return new AdminHealthSystemDto(
                env.EnvironmentName,
                version,
                buildSha,
                RuntimeInformation.FrameworkDescription,
                ProcessStartedUtc,
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
        ///
        /// <para>
        /// <b>Final review, C1:</b> <c>IsRelational()</c> answers a *provider* question — it is
        /// <c>true</c> for Npgsql whether or not Postgres is actually reachable. A migration-history
        /// read needs a live connection, so a database outage reached the two calls below anyway and
        /// threw a raw <c>NpgsqlException</c> that took the entire report down with it — including
        /// the <c>database</c> check right next to this one, which reports <c>Unhealthy</c>
        /// gracefully via <c>CanConnectAsync</c> precisely because it never throws. The
        /// <c>try</c>/<c>catch</c> below makes migrations degrade the same "cannot tell" way the
        /// non-relational branch already does, instead of taking the whole page down with it.
        /// </para>
        ///
        /// <para>
        /// <b>#453:</b> that catch declines a cancelled request, exactly as
        /// <see cref="BuildVerificationAsync"/>'s does. <c>/admin/health</c> runs every health check
        /// with no predicate, the external <c>apns</c> one included, so it is the
        /// likeliest route in the API to be abandoned mid-flight; the read then throws an
        /// <see cref="OperationCanceledException"/> carrying the caller's own token, and swallowing
        /// it logged a migration-read failure that never happened while the builder went on
        /// assembling a report for a connection that had already gone. A client hang-up is not
        /// "cannot tell" — there is no longer anyone to tell. Nothing downstream turns this into a
        /// 500: ASP.NET Core recognises the abort, never reaches <c>GlobalExceptionHandler</c>, and
        /// finishes the request as 499. Public and static for the same reason
        /// <see cref="BuildVerificationAsync"/> is — so both halves of that filter are testable
        /// without a host.
        /// </para>
        /// </summary>
        public static async Task<AdminHealthMigrationsDto> BuildMigrationsAsync(
            FlightAssistantDbContext db, bool includeDiagnostics, ILogger logger, CancellationToken ct)
        {
            if (!db.Database.IsRelational())
            {
                return new AdminHealthMigrationsDto(
                    false, 0, null, [],
                    includeDiagnostics ? "Non-relational database provider." : null);
            }

            try
            {
                var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

                return new AdminHealthMigrationsDto(
                    true,
                    applied.Count,
                    applied.Count > 0 ? applied[^1] : null,
                    pending,
                    null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to read EF Core migration state for /admin/health.");
                return new AdminHealthMigrationsDto(
                    false, 0, null, [],
                    includeDiagnostics ? $"{ex.GetType().Name}: {ex.Message}" : null);
            }
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
                includeDiagnostics ? snapshot.LastError : null,
                snapshot.LastRunDuration?.TotalSeconds);
        }

        /// <summary>
        /// The verification job's section (#367): its heartbeat, read once, and the pending count.
        /// Counting needs the database, so, like <see cref="BuildMigrationsAsync"/>, a count that
        /// throws degrades to a <c>null</c> <c>PendingCount</c> ("cannot tell") instead of taking the
        /// whole report down. Public and static so it is unit-testable without a host.
        /// </summary>
        public static async Task<AdminHealthVerificationDto> BuildVerificationAsync(
            VerificationJobHeartbeat heartbeat,
            bool enabled,
            int staleAfterDays,
            IVerificationRequestRepository requests,
            bool includeDiagnostics,
            ILogger logger,
            CancellationToken ct)
        {
            // Read once, so the response never straddles two cycles.
            var snapshot = heartbeat.Current;

            int? pendingCount;
            try
            {
                pendingCount = await requests.CountPendingAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to count pending verification requests for /admin/health.");
                pendingCount = null;
            }

            // #424: the 24-hour Error-attribution report. Degrades to null ("cannot tell") on a
            // throwing read, exactly like pendingCount above -- distinct from an empty list, which
            // means the read succeeded and nothing failed in the window (R7).
            IReadOnlyList<AdminHealthVerificationFailureDto>? recentFailures;
            try
            {
                var groups = await requests.GetRecentFailureGroupsAsync(ct);
                recentFailures = groups
                    .Select(g => new AdminHealthVerificationFailureDto(g.FailedSource, g.EntityType.ToString(), g.Count))
                    .ToList();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to read recent verification failures for /admin/health.");
                recentFailures = null;
            }

            return new AdminHealthVerificationDto(
                enabled,
                snapshot.LastRunStartedUtc,
                snapshot.LastRunCompletedUtc,
                snapshot.LastOutcome.ToString(),
                snapshot.RequestsProcessed,
                snapshot.RequestsFailed,
                snapshot.StuckAbandoned,
                snapshot.FailuresUnrecorded,
                snapshot.NextRunDueUtc,
                includeDiagnostics ? snapshot.LastError : null,
                pendingCount,
                staleAfterDays,
                snapshot.RequestsPaused,
                recentFailures);
        }
    }
}
