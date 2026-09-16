using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Verification
{
    /// <summary>
    /// Backs <c>GET /admin/verification-needs-attention</c> (#385, Task 2, controller ruling R3): a
    /// count per <c>VerificationEntityType</c> of rows in the needs-attention state (spec §2.1) --
    /// <c>IsVerified</c> false and <c>VerificationIssue</c> set to a real issue, the state left only by
    /// the verification job's mismatch and not-found paths. No parameters, nothing to validate.
    /// </summary>
    public sealed record GetVerificationNeedsAttentionQuery : IRequest<IReadOnlyList<VerificationNeedsAttentionCount>>;

    /// <summary>
    /// Reads through <see cref="IVerificationNeedsAttentionRepository.GetCountsAsync"/>, which already
    /// guarantees all three <c>VerificationEntityType</c> members, in enum order, with a count of 0
    /// rather than an omitted entry (R3) -- nothing left for the handler to add. Not audited (R6): a read.
    /// </summary>
    public sealed class GetVerificationNeedsAttentionQueryHandler
        : IRequestHandler<GetVerificationNeedsAttentionQuery, IReadOnlyList<VerificationNeedsAttentionCount>>
    {
        private readonly IVerificationNeedsAttentionRepository _repository;

        public GetVerificationNeedsAttentionQueryHandler(IVerificationNeedsAttentionRepository repository) => _repository = repository;

        public Task<IReadOnlyList<VerificationNeedsAttentionCount>> Handle(GetVerificationNeedsAttentionQuery request, CancellationToken ct) =>
            _repository.GetCountsAsync(ct);
    }
}
