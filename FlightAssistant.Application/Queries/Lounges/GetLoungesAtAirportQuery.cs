using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Services;
using MediatR;

namespace FlightAssistant.Application.Queries.Lounges
{
    public record LoungeResult(
        int Id,
        string Name,
        string? Terminal,
        string? LocationHint,
        string? Hours,
        bool IsAirside,
        bool IsDomesticOnly,
        bool IsInternationalOnly,
        string? Amenities,
        bool IsEligible,
        string? EligibleVia
    );

    public record GetLoungesAtAirportQuery(string AirportIata, Guid? TravelerId) : IRequest<IReadOnlyList<LoungeResult>>;

    public sealed class GetLoungesAtAirportQueryHandler : IRequestHandler<GetLoungesAtAirportQuery, IReadOnlyList<LoungeResult>>
    {
        private readonly ILoungeRepository _lounges;
        private readonly ILoyaltyRepository _loyalty;

        public GetLoungesAtAirportQueryHandler(ILoungeRepository lounges, ILoyaltyRepository loyalty)
        {
            _lounges = lounges;
            _loyalty = loyalty;
        }

        public async Task<IReadOnlyList<LoungeResult>> Handle(GetLoungesAtAirportQuery query, CancellationToken ct)
        {
            var lounges = await _lounges.GetByAirportAsync(query.AirportIata, ct);

            // Load eligibility data if a traveler is identified (fetch all, not paginated)
            var memberships = query.TravelerId.HasValue
                ? (await _loyalty.GetMembershipsForTravelerAsync(query.TravelerId.Value, 1, int.MaxValue, ct)).Items
                : Array.Empty<Domain.Loyalty.Entities.TravelerLoyaltyMembership>();

            var programs = memberships.Any()
                ? (await _loyalty.GetProgramsAsync(1, int.MaxValue, ct)).Items
                : Array.Empty<Domain.Loyalty.Entities.LoyaltyProgram>();

            var allTiers = programs.SelectMany(p => p.Tiers).ToList();

            return lounges.Select(lounge =>
            {
                var eligibility = LoungeEligibilityService.CheckEligibility(lounge, memberships, programs, allTiers);
                return new LoungeResult(
                    lounge.Id, lounge.Name, lounge.Terminal, lounge.LocationHint,
                    lounge.Hours, lounge.IsAirside, lounge.IsDomesticOnly, lounge.IsInternationalOnly,
                    lounge.Amenities, eligibility.IsEligible, eligibility.Via);
            }).ToList();
        }
    }
}
