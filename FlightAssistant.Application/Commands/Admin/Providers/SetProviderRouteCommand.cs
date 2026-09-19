using System.Diagnostics;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Providers.Entities;
using FlightAssistant.Domain.Verification.Enums;
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
        /// <summary>Mirrors <c>ProviderRouteConfiguration</c>'s <c>Providers</c> column
        /// (<c>varchar(256)</c>). Checked before the row is saved so an over-long joined list is a
        /// 400 naming the limit, not an unhandled <c>DbUpdateException</c> at <c>SaveChanges</c>.</summary>
        public const int MaxProvidersLength = 256;

        /// <summary>Mirrors <c>ProviderRouteConfiguration</c>'s <c>Reason</c> column
        /// (<c>varchar(512)</c>); see <see cref="MaxProvidersLength"/>'s remarks.</summary>
        public const int MaxReasonLength = 512;

        private readonly IProviderRouteRepository _repository;
        private readonly IProviderRouteDefaults _defaults;
        private readonly IVerificationSourceRegistry _registry;
        private readonly IAdminContext _adminContext;
        private readonly IAdminAuditRepository _auditRepository;
        private readonly TimeProvider _timeProvider;

        public SetProviderRouteCommandHandler(
            IProviderRouteRepository repository,
            IProviderRouteDefaults defaults,
            IVerificationSourceRegistry registry,
            IAdminContext adminContext,
            IAdminAuditRepository auditRepository,
            TimeProvider timeProvider)
        {
            _repository = repository;
            _defaults = defaults;
            _registry = registry;
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

            var options = await ValidateProvidersAsync(request, ct);

            // provider_routes.providers is varchar(256) with nothing else in front of it, so an
            // over-long joined list would otherwise reach Postgres as an unhandled
            // DbUpdateException -- a 500 naming nothing the caller could act on (the same defect
            // class #449/#455 guard against for foreign keys). Checked before the environment-ceiling
            // check below, so a malformed request is reported before an environment fact.
            var joined = string.Join(',', request.Providers);
            if (joined.Length > MaxProvidersLength)
            {
                throw new DomainException(
                    $"The combined provider list is {joined.Length} characters; the limit is {MaxProvidersLength}.",
                    400);
            }

            // A duplicate name passes the options check above -- every individual name can be a
            // valid option -- and is exactly how a short list becomes a long joined string (e.g.
            // the same provider named 25 times). Its own check and message, rather than only ever
            // surfacing as a length violation.
            var duplicate = request.Providers
                .GroupBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new DomainException($"'{duplicate.Key}' is named more than once.", 400);

            // Mirrors ProviderRouteConfiguration's Reason column (varchar(512)) -- same reasoning
            // as the providers-length check above.
            if (request.Reason is not null && request.Reason.Length > MaxReasonLength)
            {
                throw new DomainException(
                    $"'Reason' is {request.Reason.Length} characters; the limit is {MaxReasonLength}.",
                    400);
            }

            if (_defaults.IsCeilinged(request.Job))
            {
                throw new DomainException(
                    $"'{request.Job}' cannot be changed: this environment configures no alternative provider for it.",
                    409);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var changedBy = _adminContext.ActorName ?? _adminContext.ActorId;

            // Stage the audit row before the routing write -- see StageAuditAsync's own remarks.
            await StageAuditAsync(request, joined, now, ct);

            await _repository.UpsertAsync(new ProviderRoute
            {
                Job = request.Job,
                Providers = joined,
                ChangedUtc = now,
                ChangedBy = changedBy,
                Reason = request.Reason,
            }, ct);

            // Harmless no-op here: UpsertAsync's own SaveChangesAsync above already committed
            // everything StageAuditAsync staged, in the same transaction. Kept so this handler
            // still audits correctly if IProviderRouteRepository's implementation ever stops
            // saving internally.
            await _auditRepository.SaveChangesAsync(ct);

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
        /// Checks every named provider and returns the option list to report back. A flight-search
        /// key has a static option list; a verification key's options are every registered source
        /// whose <c>SupportedEntityTypes</c> includes that key's entity type, which only the registry
        /// knows — so the two kinds are validated differently on purpose.
        /// </summary>
        private Task<IReadOnlyList<string>> ValidateProvidersAsync(
            SetProviderRouteCommand request, CancellationToken ct)
        {
            if (request.Job == ProviderJobs.FlightSearchRoute)
            {
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
                return Task.FromResult(options);
            }

            var entityType = EntityTypeFor(request.Job);
            foreach (var name in request.Providers)
            {
                var source = _registry.TryGet(name);
                if (source is null)
                    throw new DomainException($"There is no verification source '{name}'.", 400);

                if (!source.SupportedEntityTypes.Contains(entityType))
                {
                    throw new DomainException(
                        $"Verification source '{name}' does not support {entityType}.", 400);
                }
            }

            var supported = ProviderJobs.SupportedSourceNames(_registry, entityType);
            return Task.FromResult(supported);
        }

        private static VerificationEntityType EntityTypeFor(string job) => job switch
        {
            ProviderJobs.VerificationAirport => VerificationEntityType.Airport,
            ProviderJobs.VerificationAirline => VerificationEntityType.Airline,
            ProviderJobs.VerificationCountry => VerificationEntityType.Country,
            _ => throw new InvalidOperationException($"'{job}' is not a verification routing key.")
        };

        /// <summary>
        /// Stages the one audit row this change produces. <c>AdminAuditInterceptor</c> excludes
        /// <see cref="ProviderRoute"/> precisely so this write can carry the operator's reason text,
        /// which its generic before/after diff has no field for — letting both fire would put two
        /// rows in the log for one change, saying different things.
        ///
        /// <para>
        /// Stages only — <see cref="IAdminAuditRepository.AddAsync"/> adds to the change tracker; it
        /// does not save — and does not call <see cref="IAdminAuditRepository.SaveChangesAsync"/>
        /// itself. <see cref="Handle"/> calls this before <see cref="IProviderRouteRepository.UpsertAsync"/>,
        /// and both repositories resolve the same scoped <c>FlightAssistantDbContext</c>, so the
        /// upsert's own <c>SaveChangesAsync</c> commits the routing row and this staged audit row
        /// together, in one transaction. Saving the audit row separately, after the upsert had
        /// already committed — this method's original shape — meant a failure or cancellation
        /// between the two calls could leave the routing change applied with no audit row: "An
        /// audit row that can roll back independently of the change it describes is worse than no
        /// audit row" (CLAUDE.md).
        /// </para>
        /// </summary>
        private async Task StageAuditAsync(
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
        }
    }
}
