using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Travelers
{
    public record TravelerProfileResult(
        Guid Id,
        string Email,
        string? DisplayName,
        string? CountryCode,
        string? NationalityCountryCode,
        string? ResidenceCountryCode
    );

    public record GetTravelerProfileQuery(Guid TravelerId) : IRequest<TravelerProfileResult?>;

    public sealed class GetTravelerProfileQueryHandler : IRequestHandler<GetTravelerProfileQuery, TravelerProfileResult?>
    {
        private readonly ITravelerRepository _travelers;

        public GetTravelerProfileQueryHandler(ITravelerRepository travelers) => _travelers = travelers;

        public async Task<TravelerProfileResult?> Handle(GetTravelerProfileQuery query, CancellationToken ct)
        {
            var t = await _travelers.GetByIdAsync(query.TravelerId, ct);
            if (t is null) return null;

            return new TravelerProfileResult(t.Id, t.Email, t.DisplayName, t.CountryCode, t.NationalityCountryCode, t.ResidenceCountryCode);
        }
    }
}
