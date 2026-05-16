using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface ICountryRepository
    {
        Task<IReadOnlyList<Country>> GetAllAsync(CancellationToken ct = default);
        Task<Country?> FindByCodeAsync(string code, CancellationToken ct = default);
        Task AddAsync(Country country, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
