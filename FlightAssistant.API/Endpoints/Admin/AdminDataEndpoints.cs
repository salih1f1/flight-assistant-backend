using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Data;
using FlightAssistant.Application.Queries.Admin.Data;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    /// <summary>
    /// The read-only admin data browser (#315). Mapped onto the shared <c>/admin</c> group, which
    /// already carries <c>RequireAuthorization("AdminOnly")</c> and <c>RequireCors("AdminPanel")</c>
    /// — neither is re-applied here.
    /// </summary>
    public static class AdminDataEndpoints
    {
        public static void MapAdminDataEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("data").WithTags("Admin - Data");

            // GET /admin/data/tables
            group.MapGet("tables", async (IMediator mediator, CancellationToken ct) =>
            {
                var summaries = await mediator.Send(new GetBrowsableTablesQuery(), ct);
                var dtos = summaries
                    .Select(s => new BrowsableTableDto(
                        s.Slug,
                        s.DisplayName,
                        s.RowCount,
                        // The kind goes out as its name, never its number: the panel keys off it.
                        s.Columns.Select(c => new BrowsableColumnDto(c.Name, c.Kind.ToString(), c.Values)).ToList()))
                    .ToList();
                return Results.Ok(dtos);
            })
            .WithName("AdminListBrowsableTables")
            .WithSummary("List every table the admin data browser can read")
            .WithDescription("Slug, display name and current row count for each table registered in BrowsableTables.")
            .Produces<List<BrowsableTableDto>>(200);

            // GET /admin/data/{table}
            group.MapGet("{table}", async (
                string table,
                [AsParameters] PagedRequest paging,
                string? sort,
                string? filter,
                IMediator mediator,
                CancellationToken ct) =>
            {
                // Resolved here, not left to the handler, specifically so an unknown slug comes
                // back as 404 rather than 400: FluentValidation's ValidationException is hard-wired
                // to 400 by GlobalExceptionHandler, which is the wrong status for "this resource
                // doesn't exist" — see GetBrowsableDataQuery.Table's doc comment for the full
                // reasoning (the handler still re-checks defensively for any other caller). Thrown
                // as a DomainException, not returned as a bare Results.NotFound(), so this path
                // renders the same ProblemDetails-with-traceId body GlobalExceptionHandler already
                // gives the handler's own defensive 404 for the identical message — matching
                // .ProducesProblem(404) below instead of an empty-bodied response.
                if (BrowsableTables.TryGetBySlug(table) is null)
                    throw new DomainException($"Unknown table '{table}'.", 404);

                var result = await mediator.Send(new GetBrowsableDataQuery(table, paging, sort, filter), ct);
                return Results.Ok(result);
            })
            .WithName("AdminGetBrowsableTableData")
            .WithSummary("Read one browsable table's rows, masked and paginated")
            .WithDescription(
                "Strictly read-only. Every column SensitiveProperties marks sensitive is replaced " +
                "with \"<redacted>\" server-side before this response is built — the full value " +
                "never leaves the API from this endpoint (a full reveal, audited, is a separate " +
                "future endpoint). 'sort' is a column name, optionally prefixed with '-' for " +
                "descending (e.g. sort=-CreatedUtc); paging is stable even without an explicit " +
                "sort, via an always-appended primary-key tiebreaker. 'filter' is 'column:value' — " +
                "exact match for a non-string column, case-insensitive contains for a string " +
                "column, split on the first ':' only so a value such as an ISO-8601 timestamp can " +
                "itself contain colons. On a string column, a value of exactly '*' means 'has a " +
                "value' — not null, and not blank (non-empty after trimming whitespace); '*' " +
                "anywhere else in the value is still a literal character to find by contains. On a " +
                "non-string column '*' is not special-cased and is simply an unparseable value, a " +
                "400. Both 'sort' and 'filter' column names are matched " +
                "case-sensitively (e.g. 'CreatedUtc', not 'createdUtc') against the table's " +
                "exposed-column whitelist and rejected with 400 if unknown, wrong-case or " +
                "sensitive — a sensitive column would otherwise let a filter be used to guess a " +
                "masked value one character at a time.")
            .Produces<PagedResult<Dictionary<string, object?>>>(200)
            .ProducesValidationProblem()
            .ProducesProblem(404);

            // POST /admin/data/{table}/{id}/reveal
            group.MapPost("{table}/{id}/reveal", async (
                string table,
                string id,
                RevealValueRequest request,
                IMediator mediator,
                HttpResponse response,
                CancellationToken ct) =>
            {
                // Same reasoning, and the same defensive re-check in the handler, as the GET
                // above: an unknown slug is a 404, decided here rather than left to
                // FluentValidation (which is hard-wired to 400 by GlobalExceptionHandler).
                if (BrowsableTables.TryGetBySlug(table) is null)
                    throw new DomainException($"Unknown table '{table}'.", 404);

                var result = await mediator.Send(new RevealValueCommand(table, id, request.Column), ct);

                // A revealed value must never rest in an intermediary cache or proxy — set on the
                // success path only, deliberately: a 400/404/500 ProblemDetails response carries no
                // value at all, so it has nothing that needs this guarantee.
                response.Headers.CacheControl = "no-store";

                return Results.Ok(new RevealedValueDto(result.Table, result.Id, result.Column, result.Value));
            })
            .WithName("AdminRevealValue")
            .WithSummary("Reveal one sensitive value, audited")
            .WithDescription(
                "The escape hatch around the data browser's server-side masking (#315). Only a " +
                "column registered in SensitiveProperties for the table's entity may be revealed " +
                "— any other column, including an already-unmasked one, is a 400. Every " +
                "successful reveal writes an admin_audit_log row (Action=Reveal) recording who " +
                "revealed which column on which row and when; Before/After are never populated " +
                "and the value itself never appears in the row, only its column name. The audit " +
                "row is committed before this response is returned — a failed audit write fails " +
                "the whole request with a 500 rather than returning a value with no audit trail. " +
                "Responses carry Cache-Control: no-store. Rate-limited per actor " +
                "(RateLimiting:AdminReveal), far tighter than the rest of the admin API: this " +
                "endpoint exists to be used rarely, not as a second browsing mode.")
            .RequireRateLimiting("admin-reveal")
            .Produces<RevealedValueDto>(200)
            .ProducesValidationProblem()
            .ProducesProblem(404);
        }
    }
}
