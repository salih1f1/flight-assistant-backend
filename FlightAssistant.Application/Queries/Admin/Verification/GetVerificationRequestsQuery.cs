using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Verification
{
    /// <summary>
    /// An entity's most recent verification requests, newest first (#385, Task 1). A "latest few",
    /// not a page (controller ruling R1): no total, and the full history stays browsable at
    /// <c>GET /admin/data/verification-requests</c>.
    /// </summary>
    /// <param name="EntityType">Exactly <c>Airport</c>, <c>Airline</c> or <c>Country</c>.</param>
    /// <param name="EntityKey">Canonicalised before matching (<see cref="VerificationRequest.TryCanonicalEntityKey"/>),
    /// so a lower-case country code finds the upper-case row.</param>
    /// <param name="Limit">Defaults to <see cref="DefaultLimit"/> when null; range 1-<see cref="MaxLimit"/>.</param>
    public sealed record GetVerificationRequestsQuery(string? EntityType, string? EntityKey, int? Limit)
        : IRequest<IReadOnlyList<VerificationRequestResult>>
    {
        public const int DefaultLimit = 5;
        public const int MaxLimit = 50;
    }

    /// <summary>One verification check of one entity, with its stored <c>differences</c>/<c>source_values</c>
    /// already deserialised (R2) rather than handed back as the raw JSON strings.</summary>
    public sealed record VerificationRequestResult(
        long Id,
        VerificationEntityType EntityType,
        string EntityKey,
        string Sources,
        VerificationTrigger Trigger,
        VerificationRequestStatus Status,
        int Attempts,
        DateTime? NextAttemptUtc,
        IReadOnlyList<VerificationDifference> Differences,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> SourceValues,
        string? Error,
        decimal EstimatedCostUsd,
        string RequestedBy,
        DateTime RequestedUtc,
        DateTime? StartedUtc,
        DateTime? CompletedUtc);

    public sealed class GetVerificationRequestsQueryValidator : AbstractValidator<GetVerificationRequestsQuery>
    {
        public GetVerificationRequestsQueryValidator()
        {
            RuleFor(x => x.EntityType)
                .Must(value => VerificationEntityTypeNames.TryParse(value, out _))
                .WithMessage(VerificationEntityTypeNames.InvalidMessage);

            // Only once the type is known: a key's canonical form depends on it.
            RuleFor(x => x.EntityKey)
                .Custom((key, context) =>
                {
                    VerificationEntityTypeNames.TryParse(context.InstanceToValidate.EntityType, out var entityType);
                    if (!VerificationRequest.TryCanonicalEntityKey(entityType, key, out _, out var error))
                        context.AddFailure(error);
                })
                .When(x => VerificationEntityTypeNames.TryParse(x.EntityType, out _));

            RuleFor(x => x.Limit)
                .InclusiveBetween(1, GetVerificationRequestsQuery.MaxLimit)
                .When(x => x.Limit.HasValue);
        }
    }

    /// <summary>
    /// Reads through <see cref="IVerificationRequestRepository.GetRecentForEntityAsync"/>: newest
    /// first, at most the resolved limit, <c>AsNoTracking</c> in the repository. An entity with no
    /// requests yet is an empty list, never a 404 -- "no checks yet" is a normal answer. Not audited
    /// (R6): this is a read.
    /// </summary>
    public sealed class GetVerificationRequestsQueryHandler
        : IRequestHandler<GetVerificationRequestsQuery, IReadOnlyList<VerificationRequestResult>>
    {
        private readonly IVerificationRequestRepository _repository;

        public GetVerificationRequestsQueryHandler(IVerificationRequestRepository repository) => _repository = repository;

        public async Task<IReadOnlyList<VerificationRequestResult>> Handle(GetVerificationRequestsQuery request, CancellationToken ct)
        {
            // The validator has already refused both; resolved again for a caller outside the pipeline.
            if (!VerificationEntityTypeNames.TryParse(request.EntityType, out var entityType))
                throw new DomainException(VerificationEntityTypeNames.InvalidMessage, 400);
            if (!VerificationRequest.TryCanonicalEntityKey(entityType, request.EntityKey, out var entityKey, out var keyError))
                throw new DomainException(keyError, 400);

            var limit = request.Limit ?? GetVerificationRequestsQuery.DefaultLimit;

            var requests = await _repository.GetRecentForEntityAsync(entityType, entityKey, limit, ct);

            return requests.Select(ToResult).ToList();
        }

        private static VerificationRequestResult ToResult(VerificationRequest r) => new(
            r.Id,
            r.EntityType,
            r.EntityKey,
            r.Sources,
            r.Trigger,
            r.Status,
            r.Attempts,
            r.NextAttemptUtc,
            r.DifferencesJson is null ? Array.Empty<VerificationDifference>() : VerificationJson.DeserializeDifferences(r.DifferencesJson),
            r.SourceValuesJson is null
                ? new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                : VerificationJson.DeserializeSourceValues(r.SourceValuesJson),
            r.Error,
            r.EstimatedCostUsd,
            r.RequestedBy,
            r.RequestedUtc,
            r.StartedUtc,
            r.CompletedUtc);
    }
}
