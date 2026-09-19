using System.Diagnostics;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Providers;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Providers.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Providers
{
    /// <summary>
    /// Changes one routing decision (#531) — what an operator presses on the panel.
    ///
    /// <para>
    /// Configuration is a hard ceiling, the same shape <c>ToggleJobCommand</c> uses: a PUT against a
    /// key whose alternatives configuration bars refuses with 409 — the caller is permitted, the
    /// environment is what refuses — and writes nothing at all, so a stored row always reflects a
    /// real operator decision rather than a rejected attempt.
    /// </para>
    /// </summary>
    public sealed record SetProviderRouteCommand(
        string Job, IReadOnlyList<string> Providers, string? Reason) : IRequest<AdminProviderRouteDto>;

    public sealed class SetProviderRouteCommandHandler
        : IRequestHandler<SetProviderRouteCommand, AdminProviderRouteDto>
    {
        private readonly IProviderRouteRepository _repository;
        private readonly IProviderRouteDefaults _defaults;
        private readonly IAdminContext _adminContext;
        private readonly IAdminAuditRepository _auditRepository;
        private readonly TimeProvider _timeProvider;

        public SetProviderRouteCommandHandler(
            IProviderRouteRepository repository,
            IProviderRouteDefaults defaults,
            IAdminContext adminContext,
            IAdminAuditRepository auditRepository,
            TimeProvider timeProvider)
        {
            _repository = repository;
            _defaults = defaults;
            _adminContext = adminContext;
            _auditRepository = auditRepository;
            _timeProvider = timeProvider;
        }

        public async Task<AdminProviderRouteDto> Handle(SetProviderRouteCommand request, CancellationToken ct)
        {
            if (!ProviderJobs.All.Contains(request.Job, StringComparer.Ordinal))
                throw new DomainException($"There is no provider routing key '{request.Job}'.", 404);

            if (request.Providers is null || request.Providers.Count == 0)
                throw new DomainException("At least one provider must be named.", 400);

            var options = ProviderJobs.OptionsFor(request.Job);
            foreach (var provider in request.Providers)
            {
                if (!options.Contains(provider, StringComparer.Ordinal))
                {
                    throw new DomainException(
                        $"'{provider}' is not a provider for '{request.Job}'. Options: {string.Join(", ", options)}.",
                        400);
                }
            }

            if (_defaults.IsCeilinged(request.Job))
            {
                throw new DomainException(
                    $"'{request.Job}' cannot be changed: this environment configures no alternative provider for it.",
                    409);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var changedBy = _adminContext.ActorName ?? _adminContext.ActorId;
            var joined = string.Join(',', request.Providers);

            await _repository.UpsertAsync(new ProviderRoute
            {
                Job = request.Job,
                Providers = joined,
                ChangedUtc = now,
                ChangedBy = changedBy,
                Reason = request.Reason,
            }, ct);

            await WriteAuditAsync(request, joined, now, ct);

            return new AdminProviderRouteDto(
                Job: request.Job,
                DisplayName: ProviderJobs.DisplayNameFor(request.Job),
                Providers: request.Providers,
                Options: options,
                IsDefault: false,
                CanChange: true,
                FixedReason: null,
                ChangedUtc: now,
                ChangedBy: changedBy,
                Reason: request.Reason);
        }

        /// <summary>
        /// Writes the one audit row this change produces. <c>AdminAuditInterceptor</c> excludes
        /// <see cref="ProviderRoute"/> precisely so this write can carry the operator's reason text,
        /// which its generic before/after diff has no field for — letting both fire would put two
        /// rows in the log for one change, saying different things.
        /// </summary>
        private async Task WriteAuditAsync(
            SetProviderRouteCommand request, string joined, DateTime now, CancellationToken ct)
        {
            var actorId = _adminContext.ActorId;
            if (string.IsNullOrWhiteSpace(actorId)) return;

            var summary = string.IsNullOrWhiteSpace(request.Reason)
                ? $"Routed {request.Job} to {joined}"
                : $"Routed {request.Job} to {joined}: {request.Reason}";

            var audit = AdminAuditLog.Create(
                actorId: actorId,
                actorName: _adminContext.ActorName,
                action: AdminAuditAction.Update,
                entityType: "ProviderRoute",
                entityId: request.Job,
                summary: summary,
                traceId: _adminContext.CorrelationId ?? Activity.Current?.Id,
                actionUtc: now);

            await _auditRepository.AddAsync(audit, ct);
            await _auditRepository.SaveChangesAsync(ct);
        }
    }
}
