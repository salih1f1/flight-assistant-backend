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

        /// <summary>
        /// Mirrors <c>ProviderRouteConfiguration</c>'s <c>ChangedBy</c> column
        /// (<c>varchar(256)</c>); see <see cref="MaxProvidersLength"/>'s remarks. Unlike the other
        /// two this value is not request content — it is <c>IAdminContext.ActorName ?? ActorId</c>,
        /// so its length is the token issuer's choice rather than the caller's, and it was the one
        /// column of the three left unbounded.
        /// </summary>
        /// <remarks>
        /// The same string also reaches <c>admin_audit_log.actor_name</c>, which is capped at 200 —
        /// tighter than this. That cap is shared by every admin write path in the repository (the
        /// interceptor writes it for all of them), so it is deliberately not re-guarded here: a
        /// second, stricter copy of a rule that exists nowhere else yet would be the drift this
        /// codebase avoids. A 201–256 character actor name therefore still fails at
        /// <c>SaveChanges</c>, on that shared path rather than this column.
        /// </remarks>
        public const int MaxChangedByLength = 256;

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

            // A key whose reader honours a single provider must not store a list.
            // ["aeroapi","aerodatabox"] otherwise passed every check here, stored as
            // "aeroapi,aerodatabox", and FlightSearchRouter then took providers[0] and discarded the
            // rest -- while GET /admin/providers echoed the whole list back as the current routing,
            // publishing a routing that was not the one in force. Which keys take a list lives in
            // ProviderJobs.TakesOrderedList, not here: a verification key legitimately takes an
            // ordered list, since each source is asked in turn.
            //
            // Last of the list checks, deliberately: a long or duplicate-laden list for a
            // single-choice key violates this rule too, and the more specific message is the more
            // actionable one ("25 characters over the limit", "'aerodatabox' is named more than
            // once") -- so this reports only the case the two above cannot, a short list of distinct
            // names for a key that honours exactly one.
            if (!ProviderJobs.TakesOrderedList(request.Job) && request.Providers.Count > 1)
            {
                throw new DomainException(
                    $"'{request.Job}' takes exactly one provider; {request.Providers.Count} were named.",
                    400);
            }

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

            // Same reasoning as the two length checks above, one step later in the method because
            // this value is not request content: it comes from the caller's token, so it is only
            // known once the environment ceiling has already had its say. Checked before
            // StageAuditAsync so a refusal still writes nothing at all.
            if (changedBy is not null && changedBy.Length > MaxChangedByLength)
            {
                throw new DomainException(
                    $"The acting admin's name is {changedBy.Length} characters; the limit is {MaxChangedByLength}.",
                    400);
            }

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
                // Identical to Providers on this path, and that is a consequence rather than a
                // shortcut: the ceiling check above already refused a ceilinged key with a 409, so
                // reaching here means nothing overrides what was just stored. GET /admin/providers
                // is where the two can differ.
                EffectiveProviders: request.Providers,
                Options: options,
                IsDefault: false,
                CanChange: true,
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

            var entityType = ProviderJobs.EntityTypeFor(request.Job);
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
