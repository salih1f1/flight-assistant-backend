using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Ci
{
    /// <summary>A CI run's test report, read from the <c>test-results</c> artifact it uploaded.</summary>
    public sealed record GetCiRunTestsQuery(long RunId) : IRequest<CiTestReport>;

    public sealed class GetCiRunTestsQueryValidator : AbstractValidator<GetCiRunTestsQuery>
    {
        public GetCiRunTestsQueryValidator()
        {
            RuleFor(x => x.RunId).GreaterThan(0);
        }
    }

    /// <summary>
    /// Two lookups, cached separately because they age differently. Which artifact a run has can
    /// change (a re-run uploads a new one), so that lookup is cached for
    /// <see cref="ArtifactLookupCacheDuration"/>. An artifact's contents never change, so the parsed
    /// report is cached by artifact id for <see cref="ReportCacheDuration"/>, and a re-run's new
    /// artifact gets a key of its own rather than being served the old attempt's report.
    /// </summary>
    public sealed class GetCiRunTestsQueryHandler : IRequestHandler<GetCiRunTestsQuery, CiTestReport>
    {
        public static readonly TimeSpan ArtifactLookupCacheDuration = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan ReportCacheDuration = TimeSpan.FromHours(1);

        private readonly ICiRunProvider _ci;
        private readonly ICacheService _cache;

        public GetCiRunTestsQueryHandler(ICiRunProvider ci, ICacheService cache)
        {
            _ci = ci;
            _cache = cache;
        }

        public async Task<CiTestReport> Handle(GetCiRunTestsQuery request, CancellationToken ct)
        {
            var artifactKey = $"ci:run-artifact:{request.RunId}";
            var artifact = await _cache.GetAsync<CiTestResultsArtifact>(artifactKey, ct);
            if (artifact is null)
            {
                // "No artifact yet" is not cached: the run is most likely still going, and its report
                // should show up as soon as it's uploaded.
                artifact = await _ci.FindTestResultsArtifactAsync(request.RunId, ct)
                    ?? throw new DomainException(
                        $"CI run {request.RunId} has no test report. It may still be running, or it " +
                        "stopped before its tests reported.",
                        404);
                await _cache.SetAsync(artifactKey, artifact, ArtifactLookupCacheDuration, ct);
            }

            if (artifact.Expired)
                throw new DomainException(
                    $"The test report for CI run {request.RunId} has expired. GitHub keeps artifacts " +
                    "for a limited time.",
                    404);

            var reportKey = $"ci:test-report:{artifact.Id}";
            var report = await _cache.GetAsync<CiTestReport>(reportKey, ct);
            if (report is null)
            {
                report = await _ci.GetTestReportAsync(artifact.Id, ct);
                await _cache.SetAsync(reportKey, report, ReportCacheDuration, ct);
            }

            return report;
        }
    }
}
