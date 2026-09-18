using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.DataSources
{
    /// <summary>
    /// Every catalogued outside dependency and its observed status (#460). Backs
    /// <c>GET /admin/health/sources</c>.
    /// </summary>
    /// <param name="IncludeDiagnostics">Whether the stored <c>LastError</c> is returned. Decided by
    /// the endpoint from <c>IHostEnvironment</c> — the same non-Production rule
    /// <c>AdminHealthReportBuilder</c> applies — rather than read here, so this layer keeps no
    /// hosting dependency of its own.</param>
    public sealed record GetDataSourcesQuery(bool IncludeDiagnostics = false)
        : IRequest<IReadOnlyList<AdminDataSourceDto>>;

    /// <summary>
    /// Left-joins the catalog onto the stored rows: the catalog decides what exists and in what
    /// order, so a source that has never been observed still appears (as Unknown), and a row left
    /// behind by a source that was removed from the catalog is ignored rather than rendered.
    /// Runs no checks — <c>GET /admin/health</c> is the endpoint that does.
    /// </summary>
    public sealed class GetDataSourcesQueryHandler
        : IRequestHandler<GetDataSourcesQuery, IReadOnlyList<AdminDataSourceDto>>
    {
        private readonly IDataSourceCatalog _catalog;
        private readonly IDataSourceStatusRepository _repository;
        private readonly TimeProvider _timeProvider;

        public GetDataSourcesQueryHandler(
            IDataSourceCatalog catalog,
            IDataSourceStatusRepository repository,
            TimeProvider timeProvider)
        {
            _catalog = catalog;
            _repository = repository;
            _timeProvider = timeProvider;
        }

        public async Task<IReadOnlyList<AdminDataSourceDto>> Handle(
            GetDataSourcesQuery request, CancellationToken ct)
        {
            var stored = (await _repository.GetAllAsync(ct))
                .GroupBy(r => r.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var now = _timeProvider.GetUtcNow();

            return _catalog.Sources
                .Select(source => DataSourceProjection.Map(source, stored.GetValueOrDefault(source.Key), now, request.IncludeDiagnostics))
                .ToList();
        }

    }
}
