using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.RefData
{
    /// <summary>One airline on the alliance detail drill-down (#457).</summary>
    public sealed record AllianceAirlineResult(
        int Id,
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode);

    /// <summary>
    /// An alliance with the airlines that belong to it, in one call — the relationship a generic
    /// browser cannot assemble, the same gap <c>GET /admin/travelers/{id}/detail</c> and
    /// <c>GET /admin/flights/{id}/detail</c> (#322) fill for their own entities. Backs
    /// <c>GET /admin/alliances/{id}/detail</c>.
    /// <para>
    /// Unlike those two there is no cap and no truncation flag, deliberately: they bound tracked
    /// flights and status events because those grow monotonically with usage, and leave memberships
    /// and device tokens whole because they do not. Airlines-per-alliance is the second kind —
    /// bounded by how many airlines exist in the alliance, about 26 for the largest real one, and it
    /// does not grow just because the app keeps running.
    /// </para>
    /// <para>
    /// It also carries no verification state. The typed admin DTOs deliberately have none —
    /// verifying lives with the raw data browser, which is where <c>PATCH /admin/alliances/{id}/verify</c>
    /// is reached from — and this route is not the place to make that inconsistent.
    /// </para>
    /// </summary>
    public sealed record AdminAllianceDetailResult(
        int Id,
        string Code,
        string Name,
        IReadOnlyList<AllianceAirlineResult> Airlines);

    public sealed record GetAdminAllianceDetailQuery(int Id) : IRequest<AdminAllianceDetailResult?>;

    public sealed class GetAdminAllianceDetailQueryHandler
        : IRequestHandler<GetAdminAllianceDetailQuery, AdminAllianceDetailResult?>
    {
        private readonly IAllianceRepository _alliances;
        private readonly IAirlineRepository _airlines;

        public GetAdminAllianceDetailQueryHandler(
            IAllianceRepository alliances, IAirlineRepository airlines)
        {
            _alliances = alliances;
            _airlines = airlines;
        }

        public async Task<AdminAllianceDetailResult?> Handle(
            GetAdminAllianceDetailQuery request, CancellationToken ct)
        {
            var alliance = await _alliances.FindByIdAsync(request.Id, ct);
            if (alliance is null)
                return null;

            // By Code, never Id: Code is the column airlines.AllianceCode references by foreign key.
            var airlines = await _airlines.GetByAllianceCodeAsync(alliance.Code, ct);

            return new AdminAllianceDetailResult(
                alliance.Id,
                alliance.Code,
                alliance.Name,
                airlines.Select(Map).ToList());
        }

        private static AllianceAirlineResult Map(Airline a) =>
            new(a.Id, a.Iata, a.Icao, a.Name, a.CountryCode);
    }
}
