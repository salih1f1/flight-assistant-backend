using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Domain.Travelers.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Travelers
{
    /// <summary>
    /// One traveler row on the typed admin list/by-id endpoints. <see cref="Email"/> is masked as
    /// <see cref="SensitiveProperties.RedactedPlaceholder"/> — see <see cref="FromEntity"/>; reveal
    /// the real value via <c>POST /admin/data/travelers/{id}/reveal</c> (#321).
    /// </summary>
    public record AdminTravelerResult(
        Guid Id,
        string Email,
        string? DisplayName,
        DateTime CreatedUtc
    )
    {
        /// <summary>
        /// Builds the result from the loaded <see cref="Traveler"/>, masking
        /// <see cref="Traveler.Email"/> per <see cref="SensitiveProperties"/> — the one place both
        /// <see cref="GetAllTravelersQueryHandler"/> and
        /// <see cref="GetAdminTravelerByIdQueryHandler"/> build this result from, so the two typed
        /// traveler endpoints cannot drift on whether <c>Email</c> is masked the way they did before
        /// #321 (both returned it in the clear, one route away from the admin data browser's
        /// masking (#315) and its own audited reveal (#318)).
        /// </summary>
        public static AdminTravelerResult FromEntity(Traveler traveler) => new(
            traveler.Id,
            SensitiveProperties.IsSensitive(typeof(Traveler), nameof(Traveler.Email))
                ? SensitiveProperties.RedactedPlaceholder
                : traveler.Email,
            traveler.DisplayName,
            traveler.CreatedUtc);
    }

    public record GetAllTravelersQuery(PagedRequest Paging) : IRequest<PagedResult<AdminTravelerResult>>;

    public class GetAllTravelersQueryValidator : AbstractValidator<GetAllTravelersQuery>
    {
        public GetAllTravelersQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    /// <summary>
    /// <b>Masks <see cref="Traveler.Email"/> here, in the Application handler</b> (via
    /// <see cref="AdminTravelerResult.FromEntity"/>) — the same posture
    /// <c>GetAdminTravelerDetailQueryHandler</c> (#322/A6) and <c>GetBrowsableDataQueryHandler</c>
    /// (#315/A4) both use, never the repository. Before #321 this handler returned
    /// <see cref="Traveler.Email"/> in the clear, which made A4's masking a speed bump rather than a
    /// control: the same value was one route away, unmasked, unlogged and unthrottled.
    /// </summary>
    public sealed class GetAllTravelersQueryHandler : IRequestHandler<GetAllTravelersQuery, PagedResult<AdminTravelerResult>>
    {
        private readonly IAdminTravelerRepository _repo;
        public GetAllTravelersQueryHandler(IAdminTravelerRepository repo) => _repo = repo;

        public async Task<PagedResult<AdminTravelerResult>> Handle(GetAllTravelersQuery request, CancellationToken ct)
        {
            var (travelers, total) = await _repo.GetAllAsync(request.Paging.Page, request.Paging.PageSize, ct);
            var items = travelers.Select(AdminTravelerResult.FromEntity).ToList();
            return new PagedResult<AdminTravelerResult>(items, total, request.Paging.Page, request.Paging.PageSize);
        }
    }
}
