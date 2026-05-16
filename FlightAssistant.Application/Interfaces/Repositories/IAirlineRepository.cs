using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAirlineRepository
    {
        Task<(IReadOnlyList<Airline> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default);
        Task<Airline?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<Airline?> FindByIataAsync(string iata, CancellationToken ct = default);
        Task<Airline?> FindByIcaoAsync(string icao, CancellationToken ct = default);
        Task AddAsync(Airline airline, CancellationToken ct = default);
        Task DeleteAsync(Airline airline, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
