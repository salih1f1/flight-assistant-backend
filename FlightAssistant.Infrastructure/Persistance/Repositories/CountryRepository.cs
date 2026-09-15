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

        public Task<Country?> FindByCodeAsync(string code, CancellationToken ct = default) =>
            _db.Countries.FirstOrDefaultAsync(c => c.Code == code.ToUpperInvariant(), ct);

        public Task<Country?> FindByAlpha3Async(string alpha3, CancellationToken ct = default) =>
            _db.Countries.FirstOrDefaultAsync(c => c.Alpha3 == alpha3.ToUpperInvariant(), ct);

        public Task<Country?> FindByNumericCodeAsync(int numericCode, CancellationToken ct = default) =>
            _db.Countries.FirstOrDefaultAsync(c => c.NumericCode == numericCode, ct);

        public async Task AddAsync(Country country, CancellationToken ct = default) =>
            await _db.Countries.AddAsync(country, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
