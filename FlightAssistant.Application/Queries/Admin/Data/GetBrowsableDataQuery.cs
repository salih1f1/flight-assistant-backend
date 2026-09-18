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
    /// <param name="Filters">
    /// Zero or more criteria, each <c>column:value</c>, <b>combined with AND</b> — every one must
    /// match (#476). On the wire this is the <c>filter</c> query parameter, repeated:
    /// <c>?filter=Country:TR&amp;filter=Name:air</c>. A single <c>filter</c> is a list of one and
    /// behaves exactly as it always has, which is what keeps every existing link and bookmark
    /// working.
    ///
    /// <para>Per criterion, unchanged: exact match for a non-string column, case-insensitive
    /// <c>contains</c> for a string column. On a string column, a value of exactly <c>*</c> means
    /// "has a value" — not null, and not blank, non-empty after trimming whitespace (#385) — while
    /// <c>*</c> anywhere else in the value, including alongside other text, stays a literal
    /// character to find by <c>contains</c>. On a non-string column <c>*</c> is not special-cased —
    /// it is simply an unparseable value, a 400. On a date column, <c>column:&gt;=value</c> means
    /// "on or after" and a null date never matches (#334); on a string column the <c>&gt;=</c>
    /// stays part of the text to find. Each criterion is split on its *first* colon only, since a
    /// value (an ISO-8601 timestamp, for instance) can itself contain colons.</para>
    ///
    /// <para>Repeats of the same column are legitimate and AND like any other pair:
    /// <c>?filter=Name:air&amp;filter=Name:tur</c> requires both substrings.</para>
    /// </param>
    public sealed record GetBrowsableDataQuery(
        string Table,
        PagedRequest Paging,
        string? Sort = null,
        IReadOnlyList<string>? Filters = null) : IRequest<PagedResult<Dictionary<string, object?>>>
    {
        /// <summary>The column name portion of <see cref="Sort"/>, with a leading <c>-</c> (if any)
        /// stripped. <c>null</c> when <see cref="Sort"/> is null or empty.</summary>
        public string? SortColumn => string.IsNullOrEmpty(Sort) ? null : (Sort[0] == '-' ? Sort[1..] : Sort);

        /// <summary>Whether <see cref="Sort"/> requested descending order (a leading <c>-</c>).</summary>
        public bool SortDescending => !string.IsNullOrEmpty(Sort) && Sort[0] == '-';

        /// <summary>
        /// The most criteria one request may carry. A bound on how much query a single URL can
        /// compose, set far above what the panel's filter form — which adds one criterion at a
        /// time — will ever produce.
        /// </summary>
        public const int MaxFilters = 10;

        /// <summary>
        /// Each of <see cref="Filters"/> split into its column and value halves on the first
        /// <c>:</c>, in the order given. A criterion that is empty or carries no colon at all is
        /// dropped here — a malformed filter, which <see cref="GetBrowsableDataQueryValidator"/>
        /// rejects with a 400 before this is ever read for real. Empty when nothing was filtered on.
        /// </summary>
        public IReadOnlyList<(string Column, string Value)> ParsedFilters =>
            (Filters ?? [])
                .Select(filter => (Filter: filter, Separator: filter?.IndexOf(':') ?? -1))
                .Where(x => x.Separator >= 0)
                .Select(x => (Column: x.Filter![..x.Separator], Value: x.Filter[(x.Separator + 1)..]))
                .ToList();
    }

    public sealed class GetBrowsableDataQueryValidator : AbstractValidator<GetBrowsableDataQuery>
    {
        public GetBrowsableDataQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());

            RuleFor(x => x.Filters)
                .Must(filters => filters is null || filters.Count <= GetBrowsableDataQuery.MaxFilters)
                .WithName("filter")
                .WithMessage($"At most {GetBrowsableDataQuery.MaxFilters} filters may be applied at once.");

            RuleForEach(x => x.Filters)
                .Must(filter => !string.IsNullOrEmpty(filter) && filter.Contains(':'))
                .WithName("filter")
                // The two-argument overload: RuleForEach hands a one-argument message lambda the
                // root query, not the element, so naming the offending criterion needs both.
                .WithMessage((_, filter) => $"Filter '{filter}' must be in the form 'column:value'.");

            RuleFor(x => x)
                .Must(BeAnExposedNonSensitiveSortColumn)
                .WithName("sort")
                .WithMessage(SortErrorMessage);

            RuleFor(x => x)
                .Must(BeAllExposedNonSensitiveFilterColumns)
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

        private static bool BeAllExposedNonSensitiveFilterColumns(GetBrowsableDataQuery query)
        {
            var table = BrowsableTables.TryGetBySlug(query.Table);
            if (table is null) return true;

            return query.ParsedFilters.All(filter =>
                BrowsableTables.IsColumnExposed(query.Table, filter.Column)
                && !SensitiveProperties.IsSensitive(table.EntityType, filter.Column));
        }

        /// <summary>
        /// One message covering every criterion that failed, each named. With several filters in
        /// play, "Invalid filter column." alone does not say which one to fix — and being told
        /// about the second only after fixing the first is the round-trip that produces.
        /// </summary>
        private static string FilterErrorMessage(GetBrowsableDataQuery query)
        {
            var table = BrowsableTables.TryGetBySlug(query.Table);
            if (table is null) return "Invalid filter column.";

            var problems = query.ParsedFilters
                .Where(filter => !BrowsableTables.IsColumnExposed(query.Table, filter.Column)
                    || SensitiveProperties.IsSensitive(table.EntityType, filter.Column))
                .Select(filter => SensitiveProperties.IsSensitive(table.EntityType, filter.Column)
                    ? $"Column '{filter.Column}' is sensitive and cannot be filtered on."
                    : $"Unknown filter column '{filter.Column}'. Valid columns: {string.Join(", ", table.Columns)}.")
                .ToList();

            return problems.Count == 0 ? "Invalid filter column." : string.Join(" ", problems);
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

            var (rows, total) = await _repository.QueryAsync(
                table,
                request.Paging.Page,
                request.Paging.PageSize,
                request.SortColumn,
                request.SortDescending,
                request.ParsedFilters,
                ct);

            // Masking and enum rendering ship here, not in the repository: every column the repository
            // handed back is checked against SensitiveProperties.IsSensitive before this method returns,
            // so the full value of a sensitive column never leaves the API from this endpoint, and an
            // enum renders as its name rather than its underlying number (#392). The repository stays
            // ignorant of both — it is a generic paginated reader, nothing about it is
            // admin-data-browser-specific.
            var items = rows.Select(row => Project(table, row)).ToList();

            return new PagedResult<Dictionary<string, object?>>(items, total, request.Paging.Page, request.Paging.PageSize);
        }

        /// <summary>
        /// Shapes one row for the response: masks sensitive columns, and renders enum values as their
        /// names (#392). An enum property arrives boxed from the repository's reflection projection, and
        /// <c>System.Text.Json</c> writes a boxed enum as its underlying number — so <c>SyncMode</c> read
        /// as <c>1</c> and audit <c>Action</c> as <c>2</c>, while every other admin surface
        /// (<c>AdminAuditEndpoints</c>, the health report, the verification endpoints) exposes the name.
        /// The browser's own filter already parses names, so the number was the odd one out in its own
        /// endpoint. Masking is checked first: a sensitive column renders as the placeholder whether or
        /// not it is an enum.
        /// </summary>
        private static Dictionary<string, object?> Project(BrowsableTable table, IReadOnlyDictionary<string, object?> row)
        {
            var projected = new Dictionary<string, object?>(row.Count);
            foreach (var (column, value) in row)
            {
                projected[column] = SensitiveProperties.IsSensitive(table.EntityType, column)
                    ? SensitiveProperties.RedactedPlaceholder
                    : value is Enum enumValue ? enumValue.ToString()
                    : value;
            }

            return projected;
        }
    }
}
