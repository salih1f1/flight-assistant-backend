using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface ICountryRepository
    {
        Task<IReadOnlyList<Country>> GetAllAsync(CancellationToken ct = default);
    }
}
