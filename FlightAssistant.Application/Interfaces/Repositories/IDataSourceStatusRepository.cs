using FlightAssistant.Domain.DataSources.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    /// <summary>
    /// The health page's access to <c>data_source_status</c> — one row per catalog key, upserted by
    /// the observer's background flush and by the operator-triggered probe.
    /// </summary>
    public interface IDataSourceStatusRepository
    {
        Task<IReadOnlyList<DataSourceStatus>> GetAllAsync(CancellationToken ct);

        Task<DataSourceStatus?> GetAsync(string key, CancellationToken ct);

        /// <summary>
        /// Inserts or overwrites the row for <c>row.Key</c>. The probe fields are only written when
        /// the incoming row carries them, so an observation flush never blanks the last probe.
        /// </summary>
        Task UpsertAsync(DataSourceStatus row, CancellationToken ct);
    }
}
