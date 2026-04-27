using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
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

    public record GetAllTravelersQuery(PagedRequest Paging) : IRequest<PagedResult<AdminTravelerResult>>;

    public class GetAllTravelersQueryValidator : AbstractValidator<GetAllTravelersQuery>
    {
        public GetAllTravelersQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    public sealed class GetAllTravelersQueryHandler : IRequestHandler<GetAllTravelersQuery, PagedResult<AdminTravelerResult>>
    {
        private readonly IAdminTravelerRepository _repo;
        public GetAllTravelersQueryHandler(IAdminTravelerRepository repo) => _repo = repo;

        public async Task<PagedResult<AdminTravelerResult>> Handle(GetAllTravelersQuery request, CancellationToken ct)
        {
            var (travelers, total) = await _repo.GetAllAsync(request.Paging.Page, request.Paging.PageSize, ct);
            var items = travelers.Select(t => new AdminTravelerResult(
                t.Id, t.Email, t.DisplayName, t.CountryCode,
                t.NationalityCountryCode, t.ResidenceCountryCode, t.CreatedUtc
            )).ToList();
            return new PagedResult<AdminTravelerResult>(items, total, request.Paging.Page, request.Paging.PageSize);
        }
    }
}
