using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Lounges
{
    public record AdminLoungeResult(
        int Id,
        int AirportId,
        string Name,
        string? Terminal,
        string? LocationHint,
        string? Hours,
        bool IsAirside,
        bool IsDomesticOnly,
        bool IsInternationalOnly,
        string? Amenities
    );

    public record GetAllLoungesAdminQuery : IRequest<IReadOnlyList<AdminLoungeResult>>;

    public sealed class GetAllLoungesAdminQueryHandler : IRequestHandler<GetAllLoungesAdminQuery, IReadOnlyList<AdminLoungeResult>>
    {
        private readonly ILoungeRepository _repo;
        public GetAllLoungesAdminQueryHandler(ILoungeRepository repo) => _repo = repo;

        public async Task<IReadOnlyList<AdminLoungeResult>> Handle(GetAllLoungesAdminQuery request, CancellationToken ct)
        {
            var lounges = await _repo.GetAllAsync(ct);
            return lounges.Select(l => new AdminLoungeResult(
                l.Id, l.AirportId, l.Name, l.Terminal, l.LocationHint, l.Hours,
                l.IsAirside, l.IsDomesticOnly, l.IsInternationalOnly, l.Amenities
            )).ToList();
        }
    }
}
