using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.Application.Queries.Visa
{
    public record VisaRequirementResult(
        string PassportCountryCode,
        string DestinationCountryCode,
        string AdmissionType,
        int? StayDays,
        string? Notes
    );

    public record GetVisaRequirementsQuery(string PassportCountryCode, string DestinationCountryCode)
        : IRequest<VisaRequirementResult>;

    public sealed class GetVisaRequirementsQueryHandler : IRequestHandler<GetVisaRequirementsQuery, VisaRequirementResult>
    {
        private readonly IVisaRequirementsProvider _provider;
        private readonly ICacheService _cache;

        public GetVisaRequirementsQueryHandler(IVisaRequirementsProvider provider, ICacheService cache)
        {
            _provider = provider;
            _cache = cache;
        }

        public async Task<VisaRequirementResult?> Handle(GetVisaRequirementsQuery query, CancellationToken ct)
        {
            var cacheKey = $"visa:{query.PassportCountryCode.ToUpperInvariant()}:{query.DestinationCountryCode.ToUpperInvariant()}";

            var cached = await _cache.GetAsync<VisaRequirementResult>(cacheKey, ct);
            if (cached is not null) return cached;

            var result = await _provider.GetRequirementsAsync(query.PassportCountryCode, query.DestinationCountryCode, ct);
            if (result is null)
                throw new DomainException(
                    "Visa requirements service returned an unexpected response. Please try again later.", 503);

            var dto = new VisaRequirementResult(result.PassportCountryCode, result.DestinationCountryCode, result.AdmissionType, result.StayDays, result.Notes);
            await _cache.SetAsync(cacheKey, dto, TimeSpan.FromHours(24), ct);
            return dto;
        }
    }
}
