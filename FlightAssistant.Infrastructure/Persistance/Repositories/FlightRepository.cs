using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class FlightRepository : IFlightRepository
    {
        public Task AddAsync(Flight entity, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<Flight?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
