using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Ci;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminCiEndpoints
    {
        /// <summary>
        /// Mapped onto the shared <c>/admin</c> group, which already carries
        /// <c>RequireAuthorization("AdminOnly")</c> and <c>RequireCors("AdminPanel")</c>; neither is
        /// re-applied here.
        /// </summary>
        public static void MapAdminCiEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("ci")
                .WithTags("Admin - CI");

            // GET /admin/ci/runs
            group.MapGet("runs", async (string? branch, int? limit, IMediator mediator, CancellationToken ct) =>
            {
                var runs = await mediator.Send(
                    new GetCiRunsQuery(branch, limit ?? GetCiRunsQuery.DefaultLimit), ct);

                return Results.Ok(runs.Select(ToDto).ToList());
            })
            .WithName("AdminGetCiRuns")
            .WithSummary("Recent CI runs")
            .WithDescription(
                "Proxies GitHub's run list for the CI workflow (GitHub:Workflow, default ci.yml), newest " +
                $"first. Optional 'branch' filters to one branch; 'limit' is 1–{GetCiRunsQuery.MaxLimit}, " +
                $"default {GetCiRunsQuery.DefaultLimit}. Cached for 60 seconds. Test counts are not part " +
                "of a run: read them from GET /admin/ci/runs/{id}/tests. 503 when GitHub:Token is not set.")
            .Produces<List<CiRunDto>>(200)
            .ProducesValidationProblem()
            .ProducesProblem(500)
            .ProducesProblem(502)
            .ProducesProblem(503);

            // GET /admin/ci/runs/{id}/tests
            group.MapGet("runs/{id:long}/tests", async (long id, IMediator mediator, CancellationToken ct) =>
            {
                var report = await mediator.Send(new GetCiRunTestsQuery(id), ct);

                return Results.Ok(new CiTestReportDto(
                    id,
                    report.Commit,
                    report.Ref,
                    report.Total,
                    report.Passed,
                    report.Failed,
                    report.Skipped,
                    report.DurationMs,
                    report.Suites
                        .Select(suite => new CiTestSuiteDto(
                            suite.Suite,
                            suite.Total,
                            suite.Passed,
                            suite.Failed,
                            suite.Skipped,
                            suite.DurationMs,
                            suite.Tests
                                .Select(test => new CiTestCaseDto(
                                    test.Name, test.Class, test.Outcome, test.DurationMs, test.Message))
                                .ToList()))
                        .ToList()));
            })
            .WithName("AdminGetCiRunTests")
            .WithSummary("A CI run's test report")
            .WithDescription(
                "Reads the test-results.json from the run's test-results artifact: totals, then each test " +
                "project with every test's outcome, duration and failure message. 404 when the run doesn't " +
                "exist, has no report (still running, or stopped before its tests reported), or its report " +
                "has expired. Cached by artifact, since an artifact never changes. 503 when GitHub:Token " +
                "is not set.")
            .Produces<CiTestReportDto>(200)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(500)
            .ProducesProblem(502)
            .ProducesProblem(503);
        }

        private static CiRunDto ToDto(CiRun run) => new(
            run.Id,
            run.RunNumber,
            run.RunAttempt,
            run.Title,
            run.Event,
            run.Branch,
            run.CommitSha,
            run.Status,
            run.Conclusion,
            run.CreatedUtc,
            run.StartedUtc,
            run.UpdatedUtc,
            run.DurationSeconds,
            run.HtmlUrl);
    }
}
