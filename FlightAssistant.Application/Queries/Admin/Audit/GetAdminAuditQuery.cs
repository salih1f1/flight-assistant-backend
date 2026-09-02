using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Domain.Administration.Enums;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Audit
{
    /// <summary>
    /// One <c>admin_audit_log</c> row. <see cref="Before"/> and <see cref="After"/> are carried as
    /// the raw <c>jsonb</c> text straight off the entity — Application has no reason to parse them;
    /// the API layer turns them into <see cref="System.Text.Json.JsonElement"/> for the wire (see
    /// <c>AdminAuditDto</c>), and <see cref="Action"/> becomes its string form there too, since
    /// <c>Contracts</c> depends on nothing and cannot reference this enum.
    /// </summary>
    /// <param name="EntityId">
    /// Null for a <see cref="AdminAuditAction.Create"/> row on an entity with a store-generated key
    /// (e.g. a Postgres identity column) — the database assigns the real value during the very
    /// write this row records, so it does not exist yet when the row is written. Such a row is
    /// never matched by an <c>EntityId</c> filter; it is identified through <see cref="After"/>
    /// instead, which holds the created entity's full property set.
    /// </param>
    public sealed record AdminAuditResult(
        Guid Id,
        string ActorId,
        string? ActorName,
        DateTime ActionUtc,
        AdminAuditAction Action,
        string EntityType,
        string? EntityId,
        string Summary,
        string? Before,
        string? After,
        string? TraceId);

    /// <summary>
    /// Lists admin-audit rows, newest first by <c>ActionUtc</c> — matching the index Task 1
    /// created, and deliberately the only sort offered: there is no use case yet for anything else,
    /// and a client-controlled sort column would be an unvalidated injection surface. All filters
    /// are optional and combine with AND. <see cref="EntityType"/> + <see cref="EntityId"/> together
    /// answer "history of this row" (the other index Task 1 created) — but see the null-EntityId
    /// caveat on <see cref="AdminAuditResult.EntityId"/>.
    /// </summary>
    public sealed record GetAdminAuditQuery(
        PagedRequest Paging,
        string? EntityType = null,
        string? EntityId = null,
        string? ActorId = null,
        AdminAuditAction? Action = null,
        DateTime? FromUtc = null,
        DateTime? ToUtc = null) : IRequest<PagedResult<AdminAuditResult>>;

    public sealed class GetAdminAuditQueryValidator : AbstractValidator<GetAdminAuditQuery>
    {
        public GetAdminAuditQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());

            // Cross-field, so it hangs off the whole request rather than either single date —
            // mirrors SearchFlightsQuery's "provide either both or neither" cross-field rules.
            RuleFor(x => x)
                .Must(x => !x.FromUtc.HasValue || !x.ToUtc.HasValue || x.FromUtc <= x.ToUtc)
                .WithName("dateRange")
                .WithMessage("FromUtc must not be after ToUtc.");
        }
    }

    public sealed class GetAdminAuditQueryHandler : IRequestHandler<GetAdminAuditQuery, PagedResult<AdminAuditResult>>
    {
        private readonly IAdminAuditRepository _repo;
        public GetAdminAuditQueryHandler(IAdminAuditRepository repo) => _repo = repo;

        public async Task<PagedResult<AdminAuditResult>> Handle(GetAdminAuditQuery request, CancellationToken ct)
        {
            var (items, total) = await _repo.SearchAsync(
                request.Paging.Page, request.Paging.PageSize,
                request.EntityType, request.EntityId, request.ActorId,
                request.Action, request.FromUtc, request.ToUtc, ct);

            var results = items.Select(a => new AdminAuditResult(
                a.Id, a.ActorId, a.ActorName, a.ActionUtc, a.Action,
                a.EntityType, a.EntityId, a.Summary, a.Before, a.After, a.TraceId)).ToList();

            return new PagedResult<AdminAuditResult>(results, total, request.Paging.Page, request.Paging.PageSize);
        }
    }
}
