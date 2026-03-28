using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Travelers
{
    public record AdminTravelerResult(
        Guid Id,
        string Email,
        string? DisplayName,
        string? CountryCode,
        string? NationalityCountryCode,
        string? ResidenceCountryCode,
        DateTime CreatedUtc
    );

    public record GetAllTravelersQuery : IRequest<IReadOnlyList<AdminTravelerResult>>;

    public sealed class GetAllTravelersQueryHandler : IRequestHandler<GetAllTravelersQuery, IReadOnlyList<AdminTravelerResult>>
    {
        private readonly IAdminTravelerRepository _repo;
        public GetAllTravelersQueryHandler(IAdminTravelerRepository repo) => _repo = repo;

        public async Task<IReadOnlyList<AdminTravelerResult>> Handle(GetAllTravelersQuery request, CancellationToken ct)
        {
            var travelers = await _repo.GetAllAsync(ct);
            return travelers.Select(t => new AdminTravelerResult(
                t.Id, t.Email, t.DisplayName, t.CountryCode,
                t.NationalityCountryCode, t.ResidenceCountryCode, t.CreatedUtc
            )).ToList();
        }
    }
}
