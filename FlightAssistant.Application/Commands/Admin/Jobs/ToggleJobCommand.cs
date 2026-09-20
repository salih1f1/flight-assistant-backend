using System.Diagnostics;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Jobs.Entities;
using MediatR;
using Microsoft.Extensions.Configuration;

namespace FlightAssistant.Application.Commands.Admin.Jobs
{
    /// <summary>
    /// Switches one job's runtime on/off switch (#471) — what an operator presses on the panel.
    ///
    /// <para>
    /// Config is a hard ceiling: <c>effective = configEnabled &amp;&amp; switchEnabled</c>. A PUT
    /// against a job whose config has it off refuses with 409, not 403 — the caller is permitted,
    /// the environment is what refuses — regardless of which direction was requested, and writes no
    /// switch row at all, so the row that does exist always reflects a real operator decision, never
    /// a rejected attempt.
    /// </para>
    /// </summary>
    public sealed record ToggleJobCommand(string Job, bool Enabled, string? Reason) : IRequest<AdminJobDto>;

    /// <summary>
    /// See <see cref="Queries.Admin.Jobs.GetJobsQueryHandler"/>'s own remarks for why
    /// <c>LastOutcome</c>/<c>LastRunUtc</c> always come back null from here too — the same
    /// API-layer split applies to this handler's result.
    /// </summary>
    public sealed class ToggleJobCommandHandler : IRequestHandler<ToggleJobCommand, AdminJobDto>
    {
        private readonly IJobSwitchRepository _repository;
        private readonly IConfiguration _configuration;
        private readonly IAdminContext _adminContext;
        private readonly IAdminAuditRepository _auditRepository;
        private readonly TimeProvider _timeProvider;

        public ToggleJobCommandHandler(
            IJobSwitchRepository repository,
            IConfiguration configuration,
            IAdminContext adminContext,
            IAdminAuditRepository auditRepository,
            TimeProvider timeProvider)
        {
            _repository = repository;
            _configuration = configuration;
            _adminContext = adminContext;
            _auditRepository = auditRepository;
            _timeProvider = timeProvider;
        }

        public async Task<AdminJobDto> Handle(ToggleJobCommand request, CancellationToken ct)
        {
            if (!JobNames.All.Contains(request.Job, StringComparer.Ordinal))
                throw new DomainException($"There is no job '{request.Job}'.", 404);

            var configKey = JobMetadata.ConfigKeyFor(request.Job);
            var configEnabled = _configuration.GetValue(configKey, true);

            if (!configEnabled)
            {
                throw new DomainException(
                    $"'{request.Job}' cannot be switched: {configKey} is false in this environment.", 409);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var changedBy = _adminContext.ActorName ?? _adminContext.ActorId;

            // Stage the audit row before the switch write -- see StageAuditAsync's own remarks.
            await StageAuditAsync(request, now, ct);

            await _repository.UpsertAsync(new JobSwitch
            {
                Job = request.Job,
                Enabled = request.Enabled,
                ChangedUtc = now,
                ChangedBy = changedBy,
                Reason = request.Reason,
            }, ct);

            // Harmless no-op here: UpsertAsync's own SaveChangesAsync above already committed
            // everything StageAuditAsync staged, in the same transaction. Kept so this handler
            // still audits correctly if IJobSwitchRepository's implementation ever stops saving
            // internally.
            await _auditRepository.SaveChangesAsync(ct);

            return new AdminJobDto(
                Job: request.Job,
                DisplayName: JobMetadata.DisplayNameFor(request.Job),
                ConfigEnabled: true,
                SwitchEnabled: request.Enabled,
                EffectiveEnabled: request.Enabled,
                CanToggle: true,
                ChangedUtc: now,
                ChangedBy: changedBy,
                Reason: request.Reason,
                LastOutcome: null,
                LastRunUtc: null);
        }

        /// <summary>
        /// Stages the one audit row this toggle produces. <c>AdminAuditInterceptor</c> excludes
        /// <see cref="JobSwitch"/> entirely (Task 2) precisely so this write can carry the operator's
        /// reason text, which the interceptor's generic before/after diff has no field for — letting
        /// both fire would put two rows in the log for one toggle, saying different things.
        ///
        /// <para>
        /// <b>Staged, not saved, and called before the switch write (#540).</b> Both repositories
        /// resolve the same scoped <c>FlightAssistantDbContext</c>, so adding the row here and
        /// letting <c>UpsertAsync</c>'s own <c>SaveChangesAsync</c> commit puts the audit row and
        /// the switch row in one transaction. Auditing afterwards — this method's original shape —
        /// meant a failure or cancellation between the two calls could leave the job switched with
        /// no record of who did it or why: "An audit row that can roll back independently of the
        /// change it describes is worse than no audit row" (CLAUDE.md). The switch is what an
        /// operator reaches for when something is already going wrong, which is exactly when that
        /// gap was most likely and least welcome.
        /// </para>
        /// </summary>
        private async Task StageAuditAsync(ToggleJobCommand request, DateTime now, CancellationToken ct)
        {
            var actorId = _adminContext.ActorId;
            if (string.IsNullOrWhiteSpace(actorId)) return;

            var state = request.Enabled ? "on" : "off";
            var summary = string.IsNullOrWhiteSpace(request.Reason)
                ? $"Switched {request.Job} {state}"
                : $"Switched {request.Job} {state}: {request.Reason}";

            var audit = AdminAuditLog.Create(
                actorId: actorId,
                actorName: _adminContext.ActorName,
                action: AdminAuditAction.Update,
                entityType: "JobSwitch",
                entityId: request.Job,
                summary: summary,
                traceId: _adminContext.CorrelationId ?? Activity.Current?.Id,
                actionUtc: now);

            // Added, not saved: Handle's UpsertAsync commits this in the same transaction as the
            // switch row.
            await _auditRepository.AddAsync(audit, ct);
        }
    }
}
