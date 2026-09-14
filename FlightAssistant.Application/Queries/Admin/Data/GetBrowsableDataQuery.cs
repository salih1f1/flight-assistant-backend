using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Data
{
    /// <summary>
    /// One page of <paramref name="Table"/>'s rows for the admin data browser, masked and
    /// paginated. Backs <c>GET /admin/data/{table}</c>.
    /// </summary>
    /// <param name="Table">
    /// A <c>BrowsableTables</c> slug. The endpoint resolves this before ever calling
    /// <see cref="MediatR.IMediator.Send{TResponse}"/> and returns 404 itself for an unknown slug —
    /// a <c>ValidationException</c> is hard-wired to 400 (see <c>GlobalExceptionHandler</c>), which
    /// is the wrong status for "this resource doesn't exist". <see cref="GetBrowsableDataQueryHandler"/>
    /// still re-resolves it defensively (a 404 <c>DomainException</c>, never trusting the endpoint's
    /// check blindly) for any caller that reaches the handler some other way.
    /// </param>
    /// <param name="Sort">
    /// A column name, optionally prefixed with <c>-</c> for descending (e.g. <c>-CreatedUtc</c>).
    /// Column names are matched case-sensitively (<see cref="StringComparer.Ordinal"/>) against the
    /// PascalCase property names in the <c>BrowsableTables</c> registry — <c>createdUtc</c> or
    /// <c>-createdutc</c> is a 400, not a lenient match. <c>null</c> for no caller-requested sort —
    /// paging is still stable in that case via the primary-key tiebreaker
    /// <see cref="IBrowsableDataRepository.QueryAsync"/> always appends.
    /// </param>
    /// <param name="Filter">
    /// <c>column:value</c> — exact match for a non-string column, case-insensitive <c>contains</c>
    /// for a string column. On a date column, <c>column:&gt;=value</c> means "on or after" and a null
    /// date never matches (#334); on a string column the <c>&gt;=</c> stays part of the text to find.
    /// Split on the *first* colon only, since a filter value (an ISO-8601 timestamp, for instance)
    /// can itself contain colons.
    /// </param>
    public sealed record GetBrowsableDataQuery(
        string Table,
        PagedRequest Paging,
        string? Sort = null,
        string? Filter = null) : IRequest<PagedResult<Dictionary<string, object?>>>
    {
        /// <summary>The column name portion of <see cref="Sort"/>, with a leading <c>-</c> (if any)
        /// stripped. <c>null</c> when <see cref="Sort"/> is null or empty.</summary>
        public string? SortColumn => string.IsNullOrEmpty(Sort) ? null : (Sort[0] == '-' ? Sort[1..] : Sort);

        /// <summary>Whether <see cref="Sort"/> requested descending order (a leading <c>-</c>).</summary>
        public bool SortDescending => !string.IsNullOrEmpty(Sort) && Sort[0] == '-';

        /// <summary>
        /// <see cref="Filter"/> split into its column and value halves on the first <c>:</c>, or
        /// <c>null</c> when <see cref="Filter"/> is empty or carries no colon at all (a malformed
        /// filter — <see cref="GetBrowsableDataQueryValidator"/> rejects that case with a 400 before
        /// this is ever read for real).
        /// </summary>
        public (string Column, string Value)? ParsedFilter
        {
            get
            {
                if (string.IsNullOrEmpty(Filter)) return null;
                var separator = Filter.IndexOf(':');
                return separator < 0 ? null : (Filter[..separator], Filter[(separator + 1)..]);
            }
        }
    }

    public sealed class GetBrowsableDataQueryValidator : AbstractValidator<GetBrowsableDataQuery>
    {
        public GetBrowsableDataQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());

            RuleFor(x => x)
                .Must(x => string.IsNullOrEmpty(x.Filter) || x.Filter.Contains(':'))
                .WithName("filter")
                .WithMessage(x => $"Filter '{x.Filter}' must be in the form 'column:value'.");

            RuleFor(x => x)
                .Must(BeAnExposedNonSensitiveSortColumn)
                .WithName("sort")
                .WithMessage(SortErrorMessage);

            RuleFor(x => x)
                .Must(BeAnExposedNonSensitiveFilterColumn)
                .WithName("filter")
                .WithMessage(FilterErrorMessage);
        }

        // Both Must() rules below deliberately treat an unresolved table slug as "valid" — table
        // existence is a 404, decided by the endpoint before this validator ever runs (see
        // GetBrowsableDataQuery.Table's doc comment), and this validator only ever produces 400s.
        // Re-flagging an already-404'd table as a sort/filter validation failure here would just
        // race the two status codes against each other for no benefit.

        private static bool BeAnExposedNonSensitiveSortColumn(GetBrowsableDataQuery query)
        {
            if (query.SortColumn is null) return true;
            var table = BrowsableTables.TryGetBySlug(query.Table);
            if (table is null) return true;

            return BrowsableTables.IsColumnExposed(query.Table, query.SortColumn)
                && !SensitiveProperties.IsSensitive(table.EntityType, query.SortColumn);
        }

        private static string SortErrorMessage(GetBrowsableDataQuery query)
        {
            var table = BrowsableTables.TryGetBySlug(query.Table);
            if (table is null || query.SortColumn is null) return "Invalid sort column.";

            if (SensitiveProperties.IsSensitive(table.EntityType, query.SortColumn))
                return $"Column '{query.SortColumn}' is sensitive and cannot be sorted on.";

            return $"Unknown sort column '{query.SortColumn}'. Valid columns: {string.Join(", ", table.Columns)}.";
        }

        private static bool BeAnExposedNonSensitiveFilterColumn(GetBrowsableDataQuery query)
        {
            var filter = query.ParsedFilter;
            if (filter is null) return true;
            var table = BrowsableTables.TryGetBySlug(query.Table);
            if (table is null) return true;

            return BrowsableTables.IsColumnExposed(query.Table, filter.Value.Column)
                && !SensitiveProperties.IsSensitive(table.EntityType, filter.Value.Column);
        }

        private static string FilterErrorMessage(GetBrowsableDataQuery query)
        {
            var table = BrowsableTables.TryGetBySlug(query.Table);
            var filter = query.ParsedFilter;
            if (table is null || filter is null) return "Invalid filter column.";

            if (SensitiveProperties.IsSensitive(table.EntityType, filter.Value.Column))
                return $"Column '{filter.Value.Column}' is sensitive and cannot be filtered on.";

            return $"Unknown filter column '{filter.Value.Column}'. Valid columns: {string.Join(", ", table.Columns)}.";
        }
    }

    public sealed class GetBrowsableDataQueryHandler
        : IRequestHandler<GetBrowsableDataQuery, PagedResult<Dictionary<string, object?>>>
    {
        private readonly IBrowsableDataRepository _repository;

        public GetBrowsableDataQueryHandler(IBrowsableDataRepository repository) => _repository = repository;

        public async Task<PagedResult<Dictionary<string, object?>>> Handle(GetBrowsableDataQuery request, CancellationToken ct)
        {
            var table = BrowsableTables.TryGetBySlug(request.Table)
                ?? throw new DomainException($"Unknown table '{request.Table}'.", 404);

            var filter = request.ParsedFilter;

            var (rows, total) = await _repository.QueryAsync(
                table,
                request.Paging.Page,
                request.Paging.PageSize,
                request.SortColumn,
                request.SortDescending,
                filter?.Column,
                filter?.Value,
                ct);

            // Masking ships here, not in the repository: every column the repository handed back
            // is checked against SensitiveProperties.IsSensitive before this method returns, so the
            // full value of a sensitive column never leaves the API from this endpoint. The
            // repository stays ignorant of redaction entirely — it is a generic paginated reader,
            // nothing about it is admin-data-browser-specific.
            var items = rows.Select(row => Mask(table, row)).ToList();

            return new PagedResult<Dictionary<string, object?>>(items, total, request.Paging.Page, request.Paging.PageSize);
        }

        private static Dictionary<string, object?> Mask(BrowsableTable table, IReadOnlyDictionary<string, object?> row)
        {
            var masked = new Dictionary<string, object?>(row.Count);
            foreach (var (column, value) in row)
            {
                masked[column] = SensitiveProperties.IsSensitive(table.EntityType, column)
                    ? SensitiveProperties.RedactedPlaceholder
                    : value;
            }

            return masked;
        }
    }
}
