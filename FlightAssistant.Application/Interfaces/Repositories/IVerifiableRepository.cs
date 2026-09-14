using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IVerifiableRepository<TEntity> where TEntity : class, IVerifiable
    {
        /// <param name="key">The entity's primary-key value in its CLR key type (e.g. an <see cref="int"/> id, or the canonical <see cref="string"/> code for <c>Country</c>) — a mismatched type makes EF throw.</param>
        Task<TEntity?> FindByKeyAsync(object key, CancellationToken ct = default);
        Task<IReadOnlyList<TEntity>> GetAllVerifiedAsync(CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
