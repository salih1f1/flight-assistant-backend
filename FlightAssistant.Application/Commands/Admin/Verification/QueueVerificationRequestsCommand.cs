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
    /// An admin queues one verification check per key (#378, spec §4.1). Keys are not checked for
    /// existence: a missing entity completes as <c>Error</c> ("Entity no longer exists") when the job
    /// claims it, which keeps a bulk enqueue one insert.
    /// </summary>
    /// <param name="EntityType">Exactly <c>Airport</c>, <c>Airline</c> or <c>Country</c>.</param>
    /// <param name="Keys">Entity keys in the form <see cref="VerificationRequest.TryCanonicalEntityKey"/> accepts;
    /// at most <see cref="MaxKeys"/>.</param>
    /// <param name="Sources">Registered source names that support <paramref name="EntityType"/>.</param>
    /// <param name="RequestedBy">The admin actor, resolved by the endpoint; stored as <c>requested_by</c>.</param>
    public sealed record QueueVerificationRequestsCommand(
        string? EntityType,
        IReadOnlyList<string>? Keys,
        IReadOnlyList<string>? Sources,
        string RequestedBy) : IRequest<VerificationEnqueueResult>
    {
        /// <summary>A panel selection is at most one page of 200; the cap bounds one request's insert.</summary>
        public const int MaxKeys = 1000;
    }

    /// <summary>
    /// Every argument the queue would otherwise throw <see cref="ArgumentException"/> on (a 500) is
    /// checked here first, by the same domain rules, so bad input is a 400.
    /// </summary>
    public sealed class QueueVerificationRequestsCommandValidator : AbstractValidator<QueueVerificationRequestsCommand>
    {
        public QueueVerificationRequestsCommandValidator(IVerificationSourceRegistry registry)
        {
            RuleFor(x => x.EntityType)
                .Must(value => VerificationEntityTypeNames.TryParse(value, out _))
                .WithMessage(VerificationEntityTypeNames.InvalidMessage);

            RuleFor(x => x.Keys)
                .NotEmpty().WithMessage("At least one key is required.")
                .Must(keys => keys is null || keys.Count <= QueueVerificationRequestsCommand.MaxKeys)
                .WithMessage($"At most {QueueVerificationRequestsCommand.MaxKeys} keys can be queued at once.");

            // Only once the type is known and the count is within the cap: a key's form depends on
            // the type, and 50,000 keys should be one error, not 50,000.
            RuleForEach(x => x.Keys)
                .Custom((key, context) =>
                {
                    VerificationEntityTypeNames.TryParse(context.InstanceToValidate.EntityType, out var entityType);
                    if (!VerificationRequest.TryCanonicalEntityKey(entityType, key, out _, out var error))
                        context.AddFailure(error);
                })
                .When(x => VerificationEntityTypeNames.TryParse(x.EntityType, out _)
                    && x.Keys is { Count: <= QueueVerificationRequestsCommand.MaxKeys });

            RuleFor(x => x.Sources)
                .NotEmpty().WithMessage("At least one source is required.");

            RuleFor(x => x.Sources)
                .Custom((sources, context) =>
                {
                    VerificationEntityTypeNames.TryParse(context.InstanceToValidate.EntityType, out var entityType);
                    if (!VerificationSourceSelection.TryResolve(registry, entityType, sources!, out _, out var error))
                        context.AddFailure(error);
                })
                .When(x => VerificationEntityTypeNames.TryParse(x.EntityType, out _) && x.Sources is { Count: > 0 });

            RuleFor(x => x.RequestedBy)
                .NotEmpty()
                .MaximumLength(VerificationRequest.MaxRequestedByLength);
        }
    }

    /// <summary>
    /// Queues through <see cref="IVerificationQueue"/> with trigger <c>admin</c> and the selected
    /// sources' summed per-row cost. The queue rows are not audited (<c>NotAuditedTypes</c>);
    /// <c>requested_by</c> is the record of who asked.
    /// </summary>
    public sealed class QueueVerificationRequestsCommandHandler
        : IRequestHandler<QueueVerificationRequestsCommand, VerificationEnqueueResult>
    {
        private readonly IVerificationQueue _queue;
        private readonly IVerificationSourceRegistry _registry;

        public QueueVerificationRequestsCommandHandler(IVerificationQueue queue, IVerificationSourceRegistry registry)
        {
            _queue = queue;
            _registry = registry;
        }

        public Task<VerificationEnqueueResult> Handle(QueueVerificationRequestsCommand request, CancellationToken ct)
        {
            // The validator has already refused both; resolved again for the parsed type and the cost.
            if (!VerificationEntityTypeNames.TryParse(request.EntityType, out var entityType))
                throw new DomainException(VerificationEntityTypeNames.InvalidMessage, 400);
            if (!VerificationSourceSelection.TryResolve(_registry, entityType, request.Sources ?? [], out var selection, out var error))
                throw new DomainException(error, 400);

            return _queue.EnqueueAsync(
                entityType,
                request.Keys ?? [],
                selection.SourceNames,
                VerificationTrigger.Admin,
                request.RequestedBy,
                selection.CostPerRequestUsd,
                ct);
        }
    }
}
