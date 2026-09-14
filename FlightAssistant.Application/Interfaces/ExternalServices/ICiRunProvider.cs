namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>One run of the CI workflow, as GitHub reports it.</summary>
    /// <param name="Status">GitHub's run status: <c>queued</c>, <c>in_progress</c>, <c>completed</c>,
    /// or a rarer one (<c>waiting</c>, <c>pending</c>, <c>requested</c>).</param>
    /// <param name="Conclusion">Null until the run completes; then <c>success</c>, <c>failure</c>,
    /// <c>cancelled</c>, <c>skipped</c>, <c>timed_out</c>, and so on.</param>
    /// <param name="StartedUtc">When the latest attempt started. A re-run moves it.</param>
    public sealed record CiRun(
        long Id,
        int RunNumber,
        int RunAttempt,
        string Title,
        string Event,
        string Branch,
        string CommitSha,
        string Status,
        string? Conclusion,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? StartedUtc,
        DateTimeOffset UpdatedUtc,
        string HtmlUrl)
    {
        /// <summary>
        /// How long the latest attempt took, for a completed run; null otherwise. GitHub's run payload
        /// carries no completion time, so this is <see cref="UpdatedUtc"/> minus
        /// <see cref="StartedUtc"/>: for a finished run, its last update is the finish.
        /// </summary>
        public int? DurationSeconds =>
            Status == "completed" && StartedUtc is { } started && UpdatedUtc >= started
                ? (int)(UpdatedUtc - started).TotalSeconds
                : null;
    }

    /// <summary>The <c>test-results</c> artifact a CI run uploaded.</summary>
    /// <param name="Expired">GitHub deletes an artifact's contents after its retention period, but
    /// keeps listing it.</param>
    public sealed record CiTestResultsArtifact(long Id, bool Expired, DateTimeOffset CreatedUtc);

    /// <summary>One test's result, as <c>.github/scripts/trx_to_json.py</c> writes it.</summary>
    /// <param name="Outcome"><c>Passed</c>, <c>Failed</c>, or another TRX outcome
    /// (<c>NotExecuted</c>, …), which the report counts as skipped.</param>
    /// <param name="Message">Failure message and stack trace, cut to 2,000 characters by the
    /// converter; empty for a test that didn't fail.</param>
    public sealed record CiTestCase(string Name, string Class, string Outcome, long DurationMs, string Message);

    /// <summary>One test project's results. <paramref name="DurationMs"/> is the sum of its tests'
    /// durations, not wall-clock time.</summary>
    public sealed record CiTestSuite(
        string Suite,
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        long DurationMs,
        IReadOnlyList<CiTestCase> Tests);

    /// <summary>A CI run's test report: the <c>test-results.json</c> inside its <c>test-results</c>
    /// artifact, as the converter wrote it.</summary>
    public sealed record CiTestReport(
        string Commit,
        string Ref,
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        long DurationMs,
        IReadOnlyList<CiTestSuite> Suites);

    /// <summary>
    /// Reads the CI workflow's runs and test reports. Defined in Application (implemented in
    /// Infrastructure against the GitHub REST API) so the admin CI queries never depend on the HTTP
    /// client or the token that serve them.
    ///
    /// <para>
    /// Every method throws <see cref="FlightAssistant.Domain.Common.Exceptions.DomainException"/>
    /// when CI results aren't configured (503), GitHub rejects the token (500), rate-limits it (503)
    /// or can't be reached (502).
    /// </para>
    /// </summary>
    public interface ICiRunProvider
    {
        /// <summary>The newest <paramref name="limit"/> runs, optionally for one branch.</summary>
        Task<IReadOnlyList<CiRun>> GetRunsAsync(string? branch, int limit, CancellationToken ct = default);

        /// <summary>The run's newest <c>test-results</c> artifact, or null when it has none (still
        /// running, or stopped before its tests reported). Throws a 404 for an unknown run.</summary>
        Task<CiTestResultsArtifact?> FindTestResultsArtifactAsync(long runId, CancellationToken ct = default);

        /// <summary>Downloads and parses an artifact's <c>test-results.json</c>. Throws a 404 when
        /// GitHub no longer has the artifact.</summary>
        Task<CiTestReport> GetTestReportAsync(long artifactId, CancellationToken ct = default);
    }
}
