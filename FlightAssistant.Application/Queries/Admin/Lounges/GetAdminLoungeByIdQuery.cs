using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Lounges;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Lounges
{
    public record GetAdminLoungeByIdQuery(int Id) : IRequest<AdminLoungeResult?>;

    public sealed class GetAdminLoungeByIdQueryHandler : IRequestHandler<GetAdminLoungeByIdQuery, AdminLoungeResult?>
    {
        private readonly ILoungeRepository _repo;
        public GetAdminLoungeByIdQueryHandler(ILoungeRepository repo) => _repo = repo;

        public async Task<AdminLoungeResult?> Handle(GetAdminLoungeByIdQuery request, CancellationToken ct)
        {
            var l = await _repo.GetByIdWithProvidersAsync(request.Id, ct);
            if (l is null) return null;
            return new AdminLoungeResult(l.Id, l.AirportId, l.Name, l.Terminal, l.LocationHint, l.Hours,
                l.IsAirside, l.IsDomesticOnly, l.IsInternationalOnly, l.Amenities);
        }
    }
}
