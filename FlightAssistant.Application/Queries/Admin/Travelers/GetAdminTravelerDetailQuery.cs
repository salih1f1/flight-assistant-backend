using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Travelers
{
    /// <summary>One device token on the traveler detail drill-down (#322). <c>Token</c> is
    /// masked — see <see cref="GetAdminTravelerDetailQueryHandler"/>.</summary>
    public sealed record AdminDeviceTokenResult(Guid Id, string Platform, string Token, DateTime LastSeenUtc);

    /// <summary>One flight the traveler tracks, joined with its <see cref="Flight"/> row so the
    /// caller never needs a second lookup per row.</summary>
    public sealed record AdminTrackedFlightResult(
        Guid TrackingId,
        int FlightId,
        string FlightNumber,
        int AirlineId,
        DateOnly ServiceDate,
        bool Notify);

    /// <summary>One loyalty membership on the traveler detail drill-down. <c>AccountNumber</c>
    /// is masked — see <see cref="GetAdminTravelerDetailQueryHandler"/>.</summary>
    public sealed record AdminMembershipResult(
        Guid Id,
        int ProgramId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate,
        DateOnly? PointsExpiryDate);

    /// <summary>
    /// The traveler with every relationship a generic browser (#315) can't assemble in one call:
    /// device tokens, tracked flights (joined with their <see cref="Flight"/> row) and loyalty
    /// memberships. Backs <c>GET /admin/travelers/{id}/detail</c>.
    /// </summary>
    public sealed record AdminTravelerDetailResult(
        Guid Id,
        string Email,
        string? DisplayName,
        DateTime CreatedUtc,
        IReadOnlyList<AdminDeviceTokenResult> DeviceTokens,
        IReadOnlyList<AdminTrackedFlightResult> TrackedFlights,
        IReadOnlyList<AdminMembershipResult> LoyaltyMemberships);

    public sealed record GetAdminTravelerDetailQuery(Guid Id) : IRequest<AdminTravelerDetailResult?>;

    /// <summary>
    /// Assembles the traveler drill-down from three repositories — <see cref="IAdminTravelerRepository"/>
    /// for the traveler and its device tokens (one query, via <c>Include</c>),
    /// <see cref="IFlightRepository"/> for tracked flights joined server-side with <see cref="Flight"/>
    /// (one query), and <see cref="ILoyaltyRepository"/> for memberships (one query) — a small, fixed
    /// number of round trips that does not grow with how many tokens/flights/memberships the traveler
    /// has, never a query per related row.
    ///
    /// <para>
    /// <b>Masking happens here, in the Application handler, not in any repository</b> — the same
    /// posture <c>GetBrowsableDataQueryHandler</c> (#315) uses. <see cref="Traveler.Email"/> and
    /// every <see cref="DeviceToken.Token"/> are registered in
    /// <see cref="SensitiveProperties"/>, so this endpoint must never let either leave in the clear —
    /// unlike <c>GET /admin/travelers</c>, which still does (#321, a known gap this endpoint does not
    /// copy). <see cref="TravelerLoyaltyMembership.AccountNumber"/> is registered sensitive too, and
    /// is masked the same way for the same reason: one registry decides what's sensitive everywhere,
    /// so this handler never hand-picks which of the three fields to protect.
    /// </para>
    /// </summary>
    public sealed class GetAdminTravelerDetailQueryHandler : IRequestHandler<GetAdminTravelerDetailQuery, AdminTravelerDetailResult?>
    {
        private const string Redacted = "<redacted>";

        private readonly IAdminTravelerRepository _travelers;
        private readonly IFlightRepository _flights;
        private readonly ILoyaltyRepository _loyalty;

        public GetAdminTravelerDetailQueryHandler(
            IAdminTravelerRepository travelers, IFlightRepository flights, ILoyaltyRepository loyalty)
        {
            _travelers = travelers;
            _flights = flights;
            _loyalty = loyalty;
        }

        public async Task<AdminTravelerDetailResult?> Handle(GetAdminTravelerDetailQuery request, CancellationToken ct)
        {
            var traveler = await _travelers.GetDetailAsync(request.Id, ct);
            if (traveler is null) return null;

            var trackedFlights = await _flights.GetAllTravelerFlightsAsync(request.Id, ct);
            var memberships = await _loyalty.GetAllMembershipsForTravelerAsync(request.Id, ct);

            return new AdminTravelerDetailResult(
                traveler.Id,
                Mask<Traveler>(nameof(Traveler.Email), traveler.Email),
                traveler.DisplayName,
                traveler.CreatedUtc,
                traveler.DeviceTokens.Select(t => new AdminDeviceTokenResult(
                    t.Id,
                    t.Platform,
                    Mask<DeviceToken>(nameof(DeviceToken.Token), t.Token),
                    t.LastSeenUtc)).ToList(),
                trackedFlights.Select(x => new AdminTrackedFlightResult(
                    x.Tracking.Id, x.Flight.Id, x.Flight.FlightNumber, x.Flight.AirlineId, x.Flight.ServiceDateUtc, x.Tracking.Notify))
                    .ToList(),
                memberships.Select(m => new AdminMembershipResult(
                    m.Id,
                    m.ProgramId,
                    Mask<TravelerLoyaltyMembership>(nameof(TravelerLoyaltyMembership.AccountNumber), m.AccountNumber),
                    m.TierId,
                    m.TierExpiryDate,
                    m.PointsExpiryDate)).ToList());
        }

        private static string Mask<TEntity>(string propertyName, string value) =>
            SensitiveProperties.IsSensitive(typeof(TEntity), propertyName) ? Redacted : value;
    }
}
