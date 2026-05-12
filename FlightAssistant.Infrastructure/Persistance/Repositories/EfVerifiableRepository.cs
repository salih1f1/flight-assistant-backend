using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class EfVerifiableRepository<TEntity> : IVerifiableRepository<TEntity>
        where TEntity : class, IVerifiable
    {
        private readonly FlightAssistantDbContext _db;
        public EfVerifiableRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<TEntity?> FindByIdAsync(int id, CancellationToken ct = default) =>
            await _db.Set<TEntity>().FindAsync(new object[] { id }, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
