using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IVerifiableRepository<TEntity> where TEntity : class, IVerifiable
    {
        Task<TEntity?> FindByIdAsync(int id, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
