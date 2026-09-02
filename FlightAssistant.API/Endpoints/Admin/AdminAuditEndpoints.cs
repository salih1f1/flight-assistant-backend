using System.Text.Json;
using FlightAssistant.Application.Queries.Admin.Audit;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Enums;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminAuditEndpoints
    {
        /// <summary>
        /// Mapped onto the shared <c>/admin</c> group, which already carries
        /// <c>RequireAuthorization("AdminOnly")</c> and <c>RequireCors("AdminPanel")</c> — neither
        /// is re-applied here.
        /// </summary>
        public static void MapAdminAuditEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("audit")
                .WithTags("Admin - Audit");

            // GET /admin/audit
            group.MapGet("", async (
                [AsParameters] PagedRequest paging,
                string? entityType,
                string? entityId,
                string? actorId,
                AdminAuditAction? action,
                DateTime? fromUtc,
                DateTime? toUtc,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(
                    new GetAdminAuditQuery(paging, entityType, entityId, actorId, action, fromUtc, toUtc),
                    ct);

                var dtos = result.Items.Select(a => new AdminAuditDto(
                    a.Id,
                    a.ActorId,
                    a.ActorName,
                    a.ActionUtc,
                    a.Action.ToString(),
                    a.EntityType,
                    a.EntityId,
                    a.Summary,
                    ToJsonElement(a.Before),
                    ToJsonElement(a.After),
                    a.TraceId)).ToList();

                return Results.Ok(new PagedResult<AdminAuditDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("AdminGetAuditLog")
            .WithSummary("List admin audit log rows")
            .WithDescription(
                "One row per admin write (#313 Tasks 1-2), newest first by ActionUtc. EntityType " +
                "and EntityId together answer 'history of this row'; ActorId and Action narrow by " +
                "who/what; FromUtc/ToUtc bound the range. All filters are optional and combine " +
                "with AND. EntityId is null on a create of an entity with a store-generated key " +
                "(the database assigns it during the very write this row records, so it does not " +
                "exist yet) — such a row can never be found through the EntityId filter and is " +
                "identified through its After payload instead, which holds the created entity's " +
                "full property set.")
            .Produces<PagedResult<AdminAuditDto>>(200)
            .ProducesValidationProblem();
        }

        /// <summary>
        /// jsonb text -&gt; <see cref="JsonElement"/>. A null column becomes a null
        /// <c>JsonElement?</c> — never <c>default(JsonElement)</c>, whose <c>ValueKind</c> is
        /// <c>Undefined</c> and which System.Text.Json throws on when asked to serialize.
        /// </summary>
        private static JsonElement? ToJsonElement(string? json) =>
            json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
    }
}
