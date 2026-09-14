using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class EfVerifiableRepository<TEntity> : IVerifiableRepository<TEntity>
        where TEntity : class, IVerifiable
    {
        private readonly FlightAssistantDbContext _db;
        public EfVerifiableRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<TEntity?> FindByKeyAsync(object key, CancellationToken ct = default) =>
            await _db.Set<TEntity>().FindAsync(new object[] { key }, ct);

        public async Task<IReadOnlyList<TEntity>> GetAllVerifiedAsync(CancellationToken ct = default) =>
            await _db.Set<TEntity>().Where(e => e.IsVerified).ToListAsync(ct);

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
