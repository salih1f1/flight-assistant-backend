using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Interfaces;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Verify
{
    public record VerifyEntityCommand<TEntity>(int Id, bool IsVerified, string VerifiedBy)
        : IRequest<bool> where TEntity : class, IVerifiable;

    public sealed class VerifyEntityCommandHandler<TEntity> : IRequestHandler<VerifyEntityCommand<TEntity>, bool>
        where TEntity : class, IVerifiable
    {
        private readonly IVerifiableRepository<TEntity> _repo;
        public VerifyEntityCommandHandler(IVerifiableRepository<TEntity> repo) => _repo = repo;

        public async Task<bool> Handle(VerifyEntityCommand<TEntity> cmd, CancellationToken ct)
        {
            var entity = await _repo.FindByIdAsync(cmd.Id, ct);
            if (entity is null) return false;

            if (cmd.IsVerified) entity.Verify(cmd.VerifiedBy);
            else entity.Unverify();

            await _repo.SaveChangesAsync(ct);
            return true;
        }
    }
}
