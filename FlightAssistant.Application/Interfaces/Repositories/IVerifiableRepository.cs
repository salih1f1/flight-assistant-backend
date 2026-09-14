using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IVerifiableRepository<TEntity> where TEntity : class, IVerifiable
    {
        Task<TEntity?> FindByKeyAsync(object key, CancellationToken ct = default);
        Task<IReadOnlyList<TEntity>> GetAllVerifiedAsync(CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
