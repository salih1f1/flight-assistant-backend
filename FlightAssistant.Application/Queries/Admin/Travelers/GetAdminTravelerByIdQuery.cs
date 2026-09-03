using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Travelers;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Travelers
{
    public record GetAdminTravelerByIdQuery(Guid Id) : IRequest<AdminTravelerResult?>;

    /// <summary>
    /// Masks <c>Email</c> via <see cref="AdminTravelerResult.FromEntity"/> — same posture as
    /// <see cref="GetAllTravelersQueryHandler"/> (#321); see that type for why the masking lives on
    /// the shared result rather than duplicated in each handler.
    /// </summary>
    public sealed class GetAdminTravelerByIdQueryHandler : IRequestHandler<GetAdminTravelerByIdQuery, AdminTravelerResult?>
    {
        private readonly IAdminTravelerRepository _repo;
        public GetAdminTravelerByIdQueryHandler(IAdminTravelerRepository repo) => _repo = repo;

        public async Task<AdminTravelerResult?> Handle(GetAdminTravelerByIdQuery request, CancellationToken ct)
        {
            var t = await _repo.GetByIdAsync(request.Id, ct);
            return t is null ? null : AdminTravelerResult.FromEntity(t);
        }
    }
}
