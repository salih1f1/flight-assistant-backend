using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.RefData
{
    /// <summary>
    /// One airline on the alliance detail drill-down (#457). <paramref name="LoyaltyProgramName"/>
    /// is the airline's own program (#495), null for an airline that runs none — which most do not.
    /// </summary>
    public sealed record AllianceAirlineResult(
        int Id,
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? LoyaltyProgramName);

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
        private readonly ILoyaltyRepository _loyalty;

        public GetAdminAllianceDetailQueryHandler(
            IAllianceRepository alliances, IAirlineRepository airlines, ILoyaltyRepository loyalty)
        {
            _alliances = alliances;
            _airlines = airlines;
            _loyalty = loyalty;
        }

        public async Task<AdminAllianceDetailResult?> Handle(
            GetAdminAllianceDetailQuery request, CancellationToken ct)
        {
            var alliance = await _alliances.FindByIdAsync(request.Id, ct);
            if (alliance is null)
                return null;

            // By Code, never Id: Code is the column airlines.AllianceCode references by foreign key.
            var airlines = await _airlines.GetByAllianceCodeAsync(alliance.Code, ct);

            // One lookup for the whole list, skipped entirely for an alliance nobody belongs to:
            // the per-airline SearchProgramsByAirlineIdAsync in a loop is the N+1 the traveler and
            // flight drill-downs are written to avoid, and this list can run to a couple of dozen.
            var programNames = airlines.Count == 0
                ? new Dictionary<int, string>()
                : await _loyalty.GetProgramNamesByAirlineIdsAsync(
                    airlines.Select(a => a.Id).ToList(), ct);

            return new AdminAllianceDetailResult(
                alliance.Id,
                alliance.Code,
                alliance.Name,
                airlines.Select(a => Map(a, programNames)).ToList());
        }

        private static AllianceAirlineResult Map(Airline a, IReadOnlyDictionary<int, string> programNames) =>
            new(a.Id, a.Iata, a.Icao, a.Name, a.CountryCode,
                programNames.TryGetValue(a.Id, out var program) ? program : null);
    }
}
