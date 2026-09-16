using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface ICountryRepository
    {
        Task<IReadOnlyList<Country>> GetAllAsync(CancellationToken ct = default);
        Task<Country?> FindByCodeAsync(string code, CancellationToken ct = default);

        /// <summary>
        /// Every country whose <see cref="Country.Code"/> matches one of <paramref name="codes"/>, in
        /// one round trip rather than one <see cref="FindByCodeAsync"/> call per code. A code with no
        /// matching row is simply absent from the result -- there is no per-code null to check. An
        /// empty <paramref name="codes"/> returns an empty list without touching the database.
        /// </summary>
        Task<IReadOnlyList<Country>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct = default);
        Task<Country?> FindByAlpha3Async(string alpha3, CancellationToken ct = default);
        Task<Country?> FindByNumericCodeAsync(int numericCode, CancellationToken ct = default);
        Task AddAsync(Country country, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
