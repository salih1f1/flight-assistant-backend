using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Verification
{
    /// <summary>
    /// An admin checks an entity again with the sources of an earlier request (#378, spec §4.4): a new
    /// <c>Pending</c> request, same entity type, key and sources. The earlier request is left as it is.
    /// </summary>
    /// <param name="RequestId">A <c>verification_requests</c> id.</param>
    /// <param name="RequestedBy">The admin actor, resolved by the endpoint; stored as <c>requested_by</c>.</param>
    public sealed record RetryVerificationRequestCommand(long RequestId, string RequestedBy) : IRequest<VerificationEnqueueResult>;

    public sealed class RetryVerificationRequestCommandValidator : AbstractValidator<RetryVerificationRequestCommand>
    {
        public RetryVerificationRequestCommandValidator()
        {
            RuleFor(x => x.RequestId).GreaterThan(0);
            RuleFor(x => x.RequestedBy)
                .NotEmpty()
                .MaximumLength(VerificationRequest.MaxRequestedByLength);
        }
    }

    /// <summary>
    /// 404 when the request doesn't exist. 400 when one of its sources is no longer registered or
    /// doesn't support the entity type: the job would complete the new request as <c>Error</c> at
    /// once, so it is refused instead. Otherwise queues through <see cref="IVerificationQueue"/> with
    /// trigger <c>admin</c> and the sources' current summed cost from the registry, and 409 when the
    /// queue skips it because the entity already has an open request (the request being retried, if
    /// it is itself still open, included).
    /// </summary>
    public sealed class RetryVerificationRequestCommandHandler
        : IRequestHandler<RetryVerificationRequestCommand, VerificationEnqueueResult>
    {
        private readonly IVerificationRequestRepository _requests;
        private readonly IVerificationQueue _queue;
        private readonly IVerificationSourceRegistry _registry;

        public RetryVerificationRequestCommandHandler(
            IVerificationRequestRepository requests, IVerificationQueue queue, IVerificationSourceRegistry registry)
        {
            _requests = requests;
            _queue = queue;
            _registry = registry;
        }

        public async Task<VerificationEnqueueResult> Handle(RetryVerificationRequestCommand command, CancellationToken ct)
        {
            var request = await _requests.GetByIdAsync(command.RequestId, ct)
                ?? throw new DomainException($"Verification request {command.RequestId} not found.", 404);

            if (!VerificationSourceSelection.TryResolve(_registry, request.EntityType, request.SourceNames, out var selection, out var error))
                throw new DomainException($"Verification request {command.RequestId} cannot be retried: {error}", 400);

            var result = await _queue.EnqueueAsync(
                request.EntityType,
                new[] { request.EntityKey },
                selection.SourceNames,
                VerificationTrigger.Admin,
                command.RequestedBy,
                selection.CostPerRequestUsd,
                ct);

            if (result.Skipped > 0)
                throw new DomainException(
                    $"{request.EntityType} {request.EntityKey} already has an open verification request.", 409);

            return result;
        }
    }
}
