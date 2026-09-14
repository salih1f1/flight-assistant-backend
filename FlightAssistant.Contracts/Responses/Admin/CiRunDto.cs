namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One run of the backend's CI workflow, as GitHub reports it.</summary>
    /// <param name="Status">GitHub's run status: <c>queued</c>, <c>in_progress</c>, <c>completed</c>,
    /// or a rarer one (<c>waiting</c>, <c>pending</c>, <c>requested</c>).</param>
    /// <param name="Conclusion">Null until the run completes; then <c>success</c>, <c>failure</c>,
    /// <c>cancelled</c>, <c>skipped</c>, <c>timed_out</c>, and so on.</param>
    /// <param name="StartedUtc">When the latest attempt started.</param>
    /// <param name="DurationSeconds">For a completed run, from the latest attempt's start to GitHub's
    /// last update of the run. Null while it runs.</param>
    /// <param name="HtmlUrl">The run on github.com.</param>
    public sealed record CiRunDto(
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
        int? DurationSeconds,
        string HtmlUrl
    );

    /// <summary>A CI run's test report, from the <c>test-results</c> artifact it uploaded.</summary>
    /// <param name="Ref">The branch or ref the run tested.</param>
    /// <param name="DurationMs">The sum of every test's duration, not the run's wall-clock time.</param>
    public sealed record CiTestReportDto(
        long RunId,
        string Commit,
        string Ref,
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        long DurationMs,
        IReadOnlyList<CiTestSuiteDto> Suites
    );

    /// <summary>One test project's results.</summary>
    /// <param name="Skipped">Every outcome other than Passed and Failed.</param>
    public sealed record CiTestSuiteDto(
        string Suite,
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        long DurationMs,
        IReadOnlyList<CiTestCaseDto> Tests
    );

    /// <summary>One test's result.</summary>
    /// <param name="Outcome"><c>Passed</c>, <c>Failed</c>, or another TRX outcome such as
    /// <c>NotExecuted</c>.</param>
    /// <param name="Message">Failure message and stack trace, up to 2,000 characters; empty when the
    /// test didn't fail.</param>
    public sealed record CiTestCaseDto(
        string Name,
        string Class,
        string Outcome,
        long DurationMs,
        string Message
    );
}
