using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Lounges.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface ILoungeRepository : IRepository<Lounge>
    {
        Task<IReadOnlyList<Lounge>> GetByAirportAsync(string airportIata, CancellationToken ct = default);
        Task<Lounge?> GetByIdWithProvidersAsync(int id, CancellationToken ct = default);
    }
}
