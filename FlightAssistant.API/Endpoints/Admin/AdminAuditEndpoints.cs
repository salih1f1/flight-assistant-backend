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
                DateTimeOffset? fromUtc,
                DateTimeOffset? toUtc,
                IMediator mediator,
                CancellationToken ct) =>
            {
                // Bound as DateTimeOffset?, not DateTime? — matches AdminUsageEndpoints
                // (AdminUsageEndpoints.cs:21-22) for the same reason: minimal ASP.NET Core model
                // binding stamps a naive "2026-09-01" or "2026-09-01T00:00:00" as
                // DateTimeKind.Unspecified, which Npgsql refuses to write against a timestamptz
                // column (ArgumentException -> unhandled -> 500). DateTimeOffset carries an
                // explicit offset (defaulting to +00:00 when the string has none), so .UtcDateTime
                // below always yields DateTimeKind.Utc — converted here, at the API boundary,
                // rather than downstream, so GetAdminAuditQuery/the repository never have to
                // reason about an ambiguous Kind at all (#313 final review, C1).
                var result = await mediator.Send(
                    new GetAdminAuditQuery(
                        paging, entityType, entityId, actorId, action,
                        fromUtc?.UtcDateTime, toUtc?.UtcDateTime),
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
                "One row per admin write (#313 Tasks 1-2), newest first by ActionUtc then Id " +
                "(a tiebreaker: many rows from the same write batch can share one ActionUtc " +
                "instant). EntityType and EntityId together answer 'history of this row'; ActorId " +
                "and Action narrow by who/what — ActorId matches case-insensitively, EntityType " +
                "does not. FromUtc/ToUtc bound the range and are both inclusive; pass an explicit " +
                "time-of-day, since a date alone (e.g. toUtc=2026-09-02) means midnight at the " +
                "start of that day and so excludes everything that happened on it. All filters are " +
                "optional and combine with AND. EntityId is null on a create of an entity with a " +
                "store-generated key (the database assigns it during the very write this row " +
                "records, so it does not exist yet) — such a row can never be found through the " +
                "EntityId filter and is identified through its After payload instead, which holds " +
                "the created entity's full property set.")
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
