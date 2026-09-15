using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Verification
{
    /// <summary>The registered verification sources that can check <see cref="EntityType"/> (#378).</summary>
    /// <param name="EntityType">Exactly <c>Airport</c>, <c>Airline</c> or <c>Country</c>.</param>
    public sealed record GetVerificationSourcesQuery(string? EntityType) : IRequest<IReadOnlyList<AdminVerificationSourceResult>>;

    /// <summary>One source an admin can pick, and what one row lookup against it costs.</summary>
    public sealed record AdminVerificationSourceResult(string Name, decimal CostPerRowUsd);

    public sealed class GetVerificationSourcesQueryValidator : AbstractValidator<GetVerificationSourcesQuery>
    {
        public GetVerificationSourcesQueryValidator()
        {
            RuleFor(x => x.EntityType)
                .Must(value => VerificationEntityTypeNames.TryParse(value, out _))
                .WithMessage($"EntityType must be one of {VerificationEntityTypeNames.Allowed}.");
        }
    }

    /// <summary>
    /// Lists the registry's sources whose <see cref="IVerificationSource.SupportedEntityTypes"/>
    /// contain the entity type, ordered by name. Empty is a valid answer: no source checks
    /// <c>Airline</c> until step 9.
    /// </summary>
    public sealed class GetVerificationSourcesQueryHandler
        : IRequestHandler<GetVerificationSourcesQuery, IReadOnlyList<AdminVerificationSourceResult>>
    {
        private readonly IVerificationSourceRegistry _registry;

        public GetVerificationSourcesQueryHandler(IVerificationSourceRegistry registry) => _registry = registry;

        public Task<IReadOnlyList<AdminVerificationSourceResult>> Handle(GetVerificationSourcesQuery request, CancellationToken ct)
        {
            // The validator has already refused this; checked again for a caller outside the pipeline.
            if (!VerificationEntityTypeNames.TryParse(request.EntityType, out var entityType))
                throw new DomainException($"EntityType must be one of {VerificationEntityTypeNames.Allowed}.", 400);

            IReadOnlyList<AdminVerificationSourceResult> sources = _registry.Sources
                .Where(source => source.SupportedEntityTypes.Contains(entityType))
                .OrderBy(source => source.Name, StringComparer.Ordinal)
                .Select(source => new AdminVerificationSourceResult(source.Name, source.CostPerRowUsd))
                .ToList();

            return Task.FromResult(sources);
        }
    }
}
