using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
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

    public record GetAllLoungesAdminQuery(PagedRequest Paging) : IRequest<PagedResult<AdminLoungeResult>>;

    public class GetAllLoungesAdminQueryValidator : AbstractValidator<GetAllLoungesAdminQuery>
    {
        public GetAllLoungesAdminQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    public sealed class GetAllLoungesAdminQueryHandler : IRequestHandler<GetAllLoungesAdminQuery, PagedResult<AdminLoungeResult>>
    {
        private readonly ILoungeRepository _repo;
        public GetAllLoungesAdminQueryHandler(ILoungeRepository repo) => _repo = repo;

        public async Task<PagedResult<AdminLoungeResult>> Handle(GetAllLoungesAdminQuery request, CancellationToken ct)
        {
            var (lounges, total) = await _repo.GetAllAsync(request.Paging.Page, request.Paging.PageSize, ct);
            var items = lounges.Select(l => new AdminLoungeResult(
                l.Id, l.AirportId, l.Name, l.Terminal, l.LocationHint, l.Hours,
                l.IsAirside, l.IsDomesticOnly, l.IsInternationalOnly, l.Amenities
            )).ToList();
            return new PagedResult<AdminLoungeResult>(items, total, request.Paging.Page, request.Paging.PageSize);
        }
    }
}
