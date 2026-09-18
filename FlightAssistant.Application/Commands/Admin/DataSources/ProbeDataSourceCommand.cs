using System.Diagnostics;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.DataSources;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.DataSources.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.DataSources
{
    /// <summary>Checks one catalogued source now, because an operator asked (#460).</summary>
    public sealed record ProbeDataSourceCommand(string Key, bool IncludeDiagnostics = false)
        : IRequest<AdminDataSourceDto>;

    /// <summary>
    /// Runs the source's probe, writes the result straight to <c>data_source_status</c> and audits
    /// the press, then returns the refreshed row.
    ///
    /// <para>
    /// The write is direct rather than through the buffered observer because the operator is
    /// waiting on the answer: an observation would not reach the table until the next flush, so the
    /// row that came back would still show the old state. The probe's own observation still lands
    /// in the buffer and simply restates this a moment later.
    /// </para>
    ///
    /// <para>
    /// This is the one write to the status table an admin causes, so it is the one that is audited
    /// — <c>AdminAuditInterceptor</c> skips the table entirely, which keeps the audit log free of a
    /// row per outbound HTTP call the application makes.
    /// </para>
    /// </summary>
    public sealed class ProbeDataSourceCommandHandler
        : IRequestHandler<ProbeDataSourceCommand, AdminDataSourceDto>
    {
        private readonly IDataSourceCatalog _catalog;
        private readonly IDataSourceStatusRepository _repository;
        private readonly IEnumerable<IDataSourceProbe> _probes;
        private readonly IAdminAuditRepository _auditRepository;
        private readonly IAdminContext _adminContext;
        private readonly TimeProvider _timeProvider;

        public ProbeDataSourceCommandHandler(
            IDataSourceCatalog catalog,
            IDataSourceStatusRepository repository,
            IEnumerable<IDataSourceProbe> probes,
            IAdminAuditRepository auditRepository,
            IAdminContext adminContext,
            TimeProvider timeProvider)
        {
            _catalog = catalog;
            _repository = repository;
            _probes = probes;
            _auditRepository = auditRepository;
            _adminContext = adminContext;
            _timeProvider = timeProvider;
        }

        public async Task<AdminDataSourceDto> Handle(ProbeDataSourceCommand request, CancellationToken ct)
        {
            var source = _catalog.TryGet(request.Key)
                ?? throw new DomainException($"There is no data source '{request.Key}'.", 404);

            if (!source.SupportsProbe)
            {
                // ProbeCostNote when there is one: the reason a source has no probe is usually that
                // running it would cost money, and the operator deserves that sentence, not "no".
                throw new DomainException(
                    source.ProbeCostNote ?? $"{source.DisplayName} cannot be checked on demand.", 400);
            }

            var probe = _probes.FirstOrDefault(p => string.Equals(p.Key, source.Key, StringComparison.Ordinal))
                ?? throw new DomainException($"{source.DisplayName} has no probe registered.", 400);

            var outcome = await probe.ProbeAsync(ct);
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            await WriteStatusAsync(source.Key, outcome, now, ct);
            await WriteAuditAsync(source.Key, source.DisplayName, outcome, ct);

            var refreshed = await _repository.GetAsync(source.Key, ct);
            return DataSourceProjection.Map(source, refreshed, _timeProvider.GetUtcNow(), request.IncludeDiagnostics);
        }

        private async Task WriteStatusAsync(string key, DataSourceOutcome outcome, DateTime now, CancellationToken ct)
        {
            var existing = await _repository.GetAsync(key, ct);
            var failed = outcome is DataSourceOutcome.Failed or DataSourceOutcome.Unauthorized;

            await _repository.UpsertAsync(new DataSourceStatus
            {
                Key = key,
                LastOutcome = outcome.ToString(),
                LastOutcomeUtc = now,
                LastOkUtc = outcome == DataSourceOutcome.Ok ? now : existing?.LastOkUtc,
                // The probe's own detail is already recorded by the observer; the stored one is kept
                // for a failure and cleared on success so a healthy row carries no stale complaint.
                LastError = failed ? existing?.LastError : null,
                ConsecutiveFailures = failed ? (existing?.ConsecutiveFailures ?? 0) + 1 : 0,
                LastProbeUtc = now,
                LastProbeBy = _adminContext.ActorName ?? _adminContext.ActorId,
            }, ct);
        }

        private async Task WriteAuditAsync(string key, string displayName, DataSourceOutcome outcome, CancellationToken ct)
        {
            var actorId = _adminContext.ActorId;
            if (string.IsNullOrWhiteSpace(actorId)) return;

            var audit = AdminAuditLog.Create(
                actorId: actorId,
                actorName: _adminContext.ActorName,
                action: AdminAuditAction.Probe,
                entityType: nameof(DataSourceStatus),
                entityId: key,
                summary: $"Checked data source {key} ({displayName}): {outcome}",
                traceId: _adminContext.CorrelationId ?? Activity.Current?.Id);

            await _auditRepository.AddAsync(audit, ct);
            await _auditRepository.SaveChangesAsync(ct);
        }
    }
}
