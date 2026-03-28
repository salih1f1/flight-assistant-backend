using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class CountryRepository : ICountryRepository
    {
        private readonly FlightAssistantDbContext _db;
        public CountryRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<IReadOnlyList<Country>> GetAllAsync(CancellationToken ct = default) =>
            await _db.Countries.OrderBy(c => c.Name).ToListAsync(ct);
    }
}
