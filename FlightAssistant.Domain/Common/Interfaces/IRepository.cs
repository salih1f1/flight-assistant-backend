namespace FlightAssistant.Domain.Common.Interfaces
{
    public interface IRepository<T> where T : IAggregateRoot
    {
        Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task AddAsync(T entity, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
