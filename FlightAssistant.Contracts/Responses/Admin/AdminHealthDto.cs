namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// Operator-facing health detail for the admin panel. Unlike <c>/health/ready</c> — a
    /// load-balancer probe that deliberately excludes checks tagged <c>external</c>, because App
    /// Service restarts the app on a 503 and a flaky FlightAware host must never be able to cause
    /// one — this runs <em>every</em> registered check, <c>aeroapi</c> and <c>apns</c> included.
    ///
    /// <para>
    /// This endpoint always responds <c>200 OK</c>, even when <see cref="Status"/> is
    /// <c>"Unhealthy"</c>. This is a report, not a probe: a non-200 response would make the panel's
    /// HTTP error handling swallow the very body that explains what broke. The overall status lives
    /// in the payload instead.
    /// </para>
    /// </summary>
    /// <param name="Status">The worst of <see cref="Checks"/>' statuses (<c>"Unhealthy"</c> beats
    /// <c>"Degraded"</c> beats <c>"Healthy"</c>); <c>"Healthy"</c> when there are no checks at
    /// all.</param>
    /// <param name="TotalDurationMs">Wall-clock time to run every check, in milliseconds.</param>
    public sealed record AdminHealthDto(
        string Status,
        long TotalDurationMs,
        IReadOnlyList<AdminHealthCheckDto> Checks,
        AdminHealthSystemDto System,
        AdminHealthMigrationsDto Migrations,
        AdminHealthFlightSyncDto FlightSync,
        AdminHealthVerificationDto Verification
    );

    /// <summary>One registered <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck"/>'s
    /// result.</summary>
    /// <param name="Status">The check's own <c>HealthStatus</c> name (<c>"Healthy"</c>,
    /// <c>"Degraded"</c> or <c>"Unhealthy"</c>).</param>
    /// <param name="Description">Author-written health-check text. Always rendered, in every
    /// environment — unlike <paramref name="Error"/>, this never carries exception detail.</param>
    /// <param name="Tags">E.g. <c>["external"]</c> for <c>aeroapi</c> and <c>apns</c> — the same tag
    /// <c>/health/ready</c> filters out.</param>
    /// <param name="Error">The check's exception, formatted <c>"{Type}: {Message}"</c> — never a
    /// stack trace. Rendered outside Production only; always <c>null</c> in Production, regardless
    /// of whether the check actually threw. Follows the same convention as
    /// <c>DomainException.DiagnosticDetail</c>.</param>
    public sealed record AdminHealthCheckDto(
        string Name,
        string Status,
        string? Description,
        long DurationMs,
        IReadOnlyList<string> Tags,
        string? Error
    );

    /// <summary>Process, build and configuration snapshot. Nothing here is a connection string or
    /// secret — <see cref="RedisConfigured"/> and <see cref="AppInsightsConfigured"/> report only
    /// whether each is configured, never the value.</summary>
    /// <param name="Version">The left-hand side of the assembly's informational version (e.g.
    /// <c>"1.0.0"</c> in <c>"1.0.0+abcdef1234"</c>).</param>
    /// <param name="BuildSha">The first 7 characters of the <c>SourceRevisionId</c> the CI publish
    /// step stamped in, or <c>null</c> on a local build — every local build lacks the informational
    /// version's <c>'+'</c> suffix entirely, so this is never a placeholder like
    /// <c>"unknown"</c>.</param>
    /// <param name="StartedUtc">Derived from the same instant as <paramref name="Uptime"/>
    /// (<c>Process.GetCurrentProcess().StartTime</c>) so the two cannot disagree.</param>
    public sealed record AdminHealthSystemDto(
        string Environment,
        string Version,
        string? BuildSha,
        string FrameworkDescription,
        DateTime StartedUtc,
        TimeSpan Uptime,
        bool RedisConfigured,
        bool AppInsightsConfigured
    );

    /// <summary>EF Core migration state.</summary>
    /// <param name="Available"><c>false</c> either on a non-relational EF Core provider (the
    /// InMemory provider used by the integration test suite throws on migration lookups) or when a
    /// relational provider's migration-history read itself throws (e.g. the database is
    /// unreachable) — both are "cannot tell", not "nothing to apply". When <c>false</c>,
    /// <see cref="AppliedCount"/>, <see cref="LastAppliedMigration"/> and <see cref="Pending"/> are
    /// left at their empty/null defaults — deliberately not faked as "no migrations pending", which
    /// is a different, stronger claim than "cannot tell".</param>
    /// <param name="Error">Why <see cref="Available"/> is <c>false</c> — either a fixed
    /// "non-relational provider" note or the migration-lookup exception, formatted like
    /// <see cref="AdminHealthCheckDto.Error"/>. Rendered outside Production only; always
    /// <c>null</c> when <see cref="Available"/> is <c>true</c>.</param>
    public sealed record AdminHealthMigrationsDto(
        bool Available,
        int AppliedCount,
        string? LastAppliedMigration,
        IReadOnlyList<string> Pending,
        string? Error
    );

    /// <summary>The flight-status sync background service's last-run heartbeat
    /// (<c>FlightSyncHeartbeat</c>), read once per request and shaped from that single snapshot so a
    /// concurrent writer can never leave this response straddling two runs.</summary>
    /// <param name="Enabled">Mirrors <c>FlightSync:Enabled</c> (default <c>true</c>) — the same
    /// config key <c>FlightStatusSyncBackgroundService</c> itself reads. When <c>false</c>, every
    /// timestamp below stays <c>null</c> by design; that must read as "disabled", not
    /// "broken".</param>
    /// <param name="LastOutcome">One of <c>"NeverRun"</c>, <c>"Running"</c>, <c>"Success"</c> or
    /// <c>"Failed"</c>.</param>
    /// <param name="FlightsProcessed">How many flights the most recently completed batch
    /// <em>attempted</em> — not how many succeeded. Succeeded = <see cref="FlightsProcessed"/> −
    /// <see cref="FlightsFailed"/>. A batch that processed every flight but had some individual
    /// flights throw is still <see cref="LastOutcome"/> <c>"Success"</c>; <see cref="FlightsFailed"/>
    /// is what signals the per-flight problem, not the sync loop itself.</param>
    /// <param name="FlightsFailed">The subset of <see cref="FlightsProcessed"/> that threw. These two
    /// fields do not sum to "batch size" plus "something else" — <see cref="FlightsFailed"/> is
    /// already counted inside <see cref="FlightsProcessed"/>.</param>
    /// <param name="LastError">Rendered outside Production only; always <c>null</c> in
    /// Production.</param>
    /// <param name="LastRunDurationSeconds">How long the most recently completed run took, in seconds, or
    /// <c>null</c> when none has completed — "never run", not "instant". Compare it with
    /// <c>FlightSync:LoopInterval</c>: a run that takes longer means each flight is polled less often than
    /// the interval suggests, because the next run starts immediately rather than on the tick (#353).</param>
    public sealed record AdminHealthFlightSyncDto(
        bool Enabled,
        DateTime? LastRunStartedUtc,
        DateTime? LastRunCompletedUtc,
        string LastOutcome,
        int FlightsProcessed,
        int FlightsFailed,
        DateTime? NextRunDueUtc,
        string? LastError,
        double? LastRunDurationSeconds
    );

    /// <summary>The reference-data verification job's last-cycle heartbeat
    /// (<c>VerificationJobHeartbeat</c>), shaped from one snapshot like
    /// <see cref="AdminHealthFlightSyncDto"/>, plus how many verification requests are waiting.</summary>
    /// <param name="Enabled">Mirrors <c>Verification:Enabled</c> (default <c>true</c>). When
    /// <c>false</c>, the timestamps below stay <c>null</c> by design: "disabled", not "broken".</param>
    /// <param name="LastOutcome">One of <c>"NeverRun"</c>, <c>"Running"</c>, <c>"Success"</c>,
    /// <c>"Paused"</c> or <c>"Failed"</c>. <c>"Failed"</c> means the cycle itself failed, not a
    /// request in it. <c>"Paused"</c> (#401 task 2, R4/R5) means the cycle worked through its batch
    /// but left <see cref="RequestsPaused"/> requests Pending because this month's AeroAPI budget is
    /// exhausted -- distinct from <c>"Success"</c> (nothing held back) and from <c>"Failed"</c> (the
    /// cycle itself did not break; it declined to spend money it doesn't have).</param>
    /// <param name="RequestsProcessed">How many requests the most recently completed cycle claimed and
    /// worked through.</param>
    /// <param name="RequestsFailed">The subset of <see cref="RequestsProcessed"/> that hit a source
    /// error (retrying, or out of attempts) or completed as an error without a retry (deleted entity,
    /// unusable source). A mismatch or not-found result is a completed check, not a failure.</param>
    /// <param name="StuckAbandoned">Claims the stuck-request reset gave up on in the last completed
    /// cycle: requests stuck <c>Running</c> that had no attempts left, so the reset completed them as
    /// <c>Error</c> instead of returning them to <c>Pending</c>. Describes the last completed cycle,
    /// exactly like <see cref="RequestsProcessed"/>.</param>
    /// <param name="FailuresUnrecorded">The subset of <see cref="RequestsFailed"/> in the last
    /// completed cycle that the job couldn't even record: those requests stayed <c>Running</c>, with
    /// no attempt counted until the next stuck-request reset picks them up. Describes the last
    /// completed cycle, exactly like <see cref="RequestsProcessed"/>.</param>
    /// <param name="LastError">Rendered outside Production only; always <c>null</c> in
    /// Production.</param>
    /// <param name="PendingCount">Requests in <c>Pending</c>, due now or waiting to retry. <c>null</c>
    /// when the count could not be read (e.g. the database is unreachable): "cannot tell", not
    /// zero.</param>
    /// <param name="RequestsPaused">The subset of the last completed cycle left exactly as they
    /// were -- still Pending, no attempt counted, no error recorded -- because they named the AeroAPI
    /// source while this month's budget was exhausted (#401 task 2, R4/R5). Not part of
    /// <see cref="RequestsFailed"/>: a pause is not a failure. Describes the last completed cycle,
    /// exactly like <see cref="RequestsProcessed"/> -- so a cycle that fails before finishing its own
    /// batch leaves this at whatever an earlier paused cycle last reported, the same way it leaves
    /// <see cref="RequestsProcessed"/> and the other counts.</param>
    public sealed record AdminHealthVerificationDto(
        bool Enabled,
        DateTime? LastRunStartedUtc,
        DateTime? LastRunCompletedUtc,
        string LastOutcome,
        int RequestsProcessed,
        int RequestsFailed,
        int StuckAbandoned,
        int FailuresUnrecorded,
        DateTime? NextRunDueUtc,
        string? LastError,
        int? PendingCount,
        int RequestsPaused = 0
    );
}
