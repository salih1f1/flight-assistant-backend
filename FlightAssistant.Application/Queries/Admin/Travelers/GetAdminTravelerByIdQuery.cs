using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Travelers;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Travelers
{
    public record GetAdminTravelerByIdQuery(Guid Id) : IRequest<AdminTravelerResult?>;

    public sealed class GetAdminTravelerByIdQueryHandler : IRequestHandler<GetAdminTravelerByIdQuery, AdminTravelerResult?>
    {
        private readonly IAdminTravelerRepository _repo;
        public GetAdminTravelerByIdQueryHandler(IAdminTravelerRepository repo) => _repo = repo;

        public async Task<AdminTravelerResult?> Handle(GetAdminTravelerByIdQuery request, CancellationToken ct)
        {
            var t = await _repo.GetByIdAsync(request.Id, ct);
            if (t is null) return null;
            return new AdminTravelerResult(t.Id, t.Email, t.DisplayName, t.CountryCode,
                t.NationalityCountryCode, t.ResidenceCountryCode, t.CreatedUtc);
        }
    }
}
