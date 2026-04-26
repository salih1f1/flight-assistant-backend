namespace FlightAssistant.Domain.Common.Interfaces
{
    public interface IRepository<T> where T : IAggregateRoot
    {
        Task AddAsync(T entity, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
