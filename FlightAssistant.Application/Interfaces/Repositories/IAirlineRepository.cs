using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAirlineRepository
    {
        Task<(IReadOnlyList<Airline> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default);
        Task<Airline?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<Airline?> FindByIataAsync(string iata, CancellationToken ct = default);
        Task<Airline?> FindByIcaoAsync(string icao, CancellationToken ct = default);

        /// <summary>
        /// Every airline in the alliance with this <c>Code</c>, the column
        /// <c>airlines.AllianceCode</c> references by foreign key. Ordered by name then id:
        /// PostgreSQL guarantees no order of its own, and an admin panel re-rendering the same
        /// alliance in a different order between two identical calls is churn with no cause.
        /// </summary>
        Task<IReadOnlyList<Airline>> GetByAllianceCodeAsync(string allianceCode, CancellationToken ct = default);
        Task AddAsync(Airline airline, CancellationToken ct = default);
        Task DeleteAsync(Airline airline, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
