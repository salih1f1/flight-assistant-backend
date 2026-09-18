using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;
using Microsoft.Extensions.Configuration;

namespace FlightAssistant.Application.Queries.Admin.Jobs
{
    /// <summary>
    /// Both switchable jobs' config ceiling, runtime switch, and derived effective state (#471).
    /// Backs <c>GET /admin/jobs</c>.
    ///
    /// <para>
    /// Carries no parameters, and this handler always returns null for <c>LastOutcome</c> and
    /// <c>LastRunUtc</c>: <c>FlightSyncHeartbeat</c> and <c>VerificationJobHeartbeat</c> are hosting
    /// artifacts that live in the API layer with, per CLAUDE.md, "no cross-layer interface" — the
    /// same reason <c>/admin/health</c> reads them directly rather than through MediatR.
    /// <c>AdminJobEndpoints</c> fills those two fields in after this handler returns.
    /// </para>
    /// </summary>
    public sealed record GetJobsQuery : IRequest<IReadOnlyList<AdminJobDto>>;

    public sealed class GetJobsQueryHandler : IRequestHandler<GetJobsQuery, IReadOnlyList<AdminJobDto>>
    {
        private readonly IJobSwitch _jobSwitch;
        private readonly IConfiguration _configuration;

        public GetJobsQueryHandler(IJobSwitch jobSwitch, IConfiguration configuration)
        {
            _jobSwitch = jobSwitch;
            _configuration = configuration;
        }

        public async Task<IReadOnlyList<AdminJobDto>> Handle(GetJobsQuery request, CancellationToken ct)
        {
            var jobs = new List<AdminJobDto>(JobNames.All.Count);
            foreach (var job in JobNames.All)
                jobs.Add(await BuildAsync(job, ct));
            return jobs;
        }

        private async Task<AdminJobDto> BuildAsync(string job, CancellationToken ct)
        {
            // Same key and default the owning hosted service itself reads (JobMetadata's own remarks).
            var configEnabled = _configuration.GetValue(JobMetadata.ConfigKeyFor(job), true);
            var state = await _jobSwitch.GetAsync(job, ct);

            return new AdminJobDto(
                Job: job,
                DisplayName: JobMetadata.DisplayNameFor(job),
                ConfigEnabled: configEnabled,
                SwitchEnabled: state.Enabled,
                EffectiveEnabled: configEnabled && state.Enabled,
                CanToggle: configEnabled,
                ChangedUtc: state.ChangedUtc,
                ChangedBy: state.ChangedBy,
                Reason: state.Reason,
                LastOutcome: null,
                LastRunUtc: null);
        }
    }
}
