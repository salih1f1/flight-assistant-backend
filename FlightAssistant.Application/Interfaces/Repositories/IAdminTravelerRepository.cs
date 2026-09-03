using FlightAssistant.Domain.Travelers.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAdminTravelerRepository
    {
        Task<(IReadOnlyList<Traveler> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default);
        Task<Traveler?> GetByIdAsync(Guid id, CancellationToken ct = default);

        /// <summary>
        /// The traveler and their device tokens for the admin drill-down (#322), read
        /// <c>AsNoTracking</c> — unlike <see cref="GetByIdAsync"/>, which stays tracked because
        /// <see cref="DeleteAsync"/> and the device-token delete both need to mutate the result.
        /// </summary>
        Task<Traveler?> GetDetailAsync(Guid id, CancellationToken ct = default);

        Task DeleteAsync(Traveler traveler, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
