using System.Reflection;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Data
{
    /// <summary>One row of <c>GET /admin/data/tables</c>: a browsable table's slug, display name,
    /// current row count and exposed columns.</summary>
    /// <param name="Columns">Described from the entity type's real properties (#451), in the
    /// registry's own order, so nothing about a column's kind is hand-maintained here.</param>
    public sealed record BrowsableTableSummary(
        string Slug,
        string DisplayName,
        int RowCount,
        IReadOnlyList<BrowsableColumn> Columns);

    /// <summary>
    /// Lists every table the admin data browser can read, with a live row count for each. Backs
    /// <c>GET /admin/data/tables</c> — the index the panel uses to build its table picker.
    /// </summary>
    public sealed record GetBrowsableTablesQuery : IRequest<IReadOnlyList<BrowsableTableSummary>>;

    public sealed class GetBrowsableTablesQueryHandler
        : IRequestHandler<GetBrowsableTablesQuery, IReadOnlyList<BrowsableTableSummary>>
    {
        private readonly IBrowsableDataRepository _repository;

        public GetBrowsableTablesQueryHandler(IBrowsableDataRepository repository) => _repository = repository;

        // One COUNT per table (19 today) rather than a single batched query: this endpoint is an
        // admin diagnostic index, not a hot path, and BrowsableTables.All spans entity types with
        // no common base to batch a dynamic COUNT across without the same reflection machinery
        // QueryAsync already needs — not worth duplicating for a call nobody is paging through
        // repeatedly.
        public async Task<IReadOnlyList<BrowsableTableSummary>> Handle(GetBrowsableTablesQuery request, CancellationToken ct)
        {
            var summaries = new List<BrowsableTableSummary>(BrowsableTables.All.Count);

            foreach (var table in BrowsableTables.All)
            {
                var count = await _repository.CountAsync(table, ct);
                summaries.Add(new BrowsableTableSummary(table.Slug, table.DisplayName, count, Describe(table)));
            }

            return summaries;
        }

        /// <summary>
        /// Every exposed column, described from the property it names. The registry's column names
        /// are <c>nameof</c>-captured against the same entity type and <c>BrowsableTablesTests</c>
        /// fails the build if one stops resolving, so a null property here is impossible rather than
        /// merely unlikely — hence the null-forgiving lookup rather than a skip that would silently
        /// shorten the list.
        /// </summary>
        private static IReadOnlyList<BrowsableColumn> Describe(BrowsableTable table) =>
            table.Columns
                .Select(name => BrowsableColumnKinds.Describe(
                    table.EntityType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.PropertyType, name))
                .ToList();
    }
}
