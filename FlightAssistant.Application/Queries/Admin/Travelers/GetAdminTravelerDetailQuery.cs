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
    /// caller never needs a second lookup per row — <c>DepartureAirportId</c>/<c>ArrivalAirportId</c>
    /// are included for exactly that reason (#322 fix round, M6): the join already loads the whole
    /// <see cref="Flight"/> row, so drawing a JFK→LAX-style route on this list should never cost a
    /// second call to <c>/admin/flights/{id}/detail</c> per row just to learn the two airport
    /// ids.</summary>
    public sealed record AdminTrackedFlightResult(
        Guid TrackingId,
        int FlightId,
        string FlightNumber,
        int AirlineId,
        int DepartureAirportId,
        int ArrivalAirportId,
        DateOnly ServiceDate,
        bool Notify);

    /// <summary>One loyalty membership on the traveler detail drill-down. <c>AccountNumber</c>
    /// is masked — see <see cref="GetAdminTravelerDetailQueryHandler"/>.</summary>
    public sealed record AdminMembershipResult(
        Guid Id,
        int ProgramId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate);

    /// <summary>
    /// The traveler with every relationship a generic browser (#315) can't assemble in one call:
    /// device tokens, tracked flights (joined with their <see cref="Flight"/> row) and loyalty
    /// memberships. Backs <c>GET /admin/travelers/{id}/detail</c>. <c>TrackedFlightsTruncated</c> is
    /// true when <see cref="TrackedFlights"/> was cut off at
    /// <see cref="GetAdminTravelerDetailQueryHandler.MaxTrackedFlights"/> — see that constant's own
    /// doc comment for why tracked flights is the one relationship here worth bounding at all.
    /// </summary>
    public sealed record AdminTravelerDetailResult(
        Guid Id,
        string Email,
        string? DisplayName,
        DateTime CreatedUtc,
        IReadOnlyList<AdminDeviceTokenResult> DeviceTokens,
        IReadOnlyList<AdminTrackedFlightResult> TrackedFlights,
        bool TrackedFlightsTruncated,
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
        /// <summary>
        /// Tracked flights is the only one of the three traveler-detail relationships worth
        /// bounding (#322 fix round, M5). Loyalty memberships are schema-bounded — at most one per
        /// <c>loyalty_programs</c> row, via <c>UNIQUE(TravelerId, ProgramId)</c> — and device tokens
        /// are tiny in practice, so both stay unpaged. <c>traveler_flights</c> has no such ceiling:
        /// its only removal path is an explicit <c>UntrackFlightCommand</c> and there is no
        /// retention/purge job anywhere in the codebase, so it grows monotonically with usage.
        /// Capped the same way as
        /// <see cref="FlightAssistant.Application.Queries.Admin.Flights.GetAdminFlightDetailQueryHandler.MaxStatusHistory"/>
        /// — a real server-side <c>LIMIT</c> keeping the newest rows, plus a truncation flag on the
        /// result — for the same reason: <c>GET /admin/data/traveler-flights</c> (A4) already pages
        /// the full relationship, so this drill-down can stay the "recent" view and point a caller
        /// at the browser for the rest instead of trying to be the archive itself.
        /// </summary>
        public const int MaxTrackedFlights = 50;

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

            // Fetch one more than the cap so "truncated" is exact rather than an inference.
            // Comparing Count == MaxTrackedFlights would report a traveler with precisely
            // MaxTrackedFlights flights as truncated when it is complete, sending an operator to
            // the generic browser for rows that are not there.
            var fetchedFlights = await _flights.GetRecentTravelerFlightsAsync(request.Id, MaxTrackedFlights + 1, ct);
            var trackedFlightsTruncated = fetchedFlights.Count > MaxTrackedFlights;
            var trackedFlights = fetchedFlights.Take(MaxTrackedFlights).ToList();
            var memberships = await _loyalty.GetAllMembershipsForTravelerAsync(request.Id, ct);

            return new AdminTravelerDetailResult(
                traveler.Id,
                Mask<Traveler>(nameof(Traveler.Email), traveler.Email),
                traveler.DisplayName,
                traveler.CreatedUtc,
                // Ordered client-side, not via the repository's Include: PostgreSQL gives no row
                // order guarantee without an explicit ORDER BY, so two identical calls could
                // otherwise return the same tokens in a different order (#322 fix round, M4). A
                // stable sort here is enough to make the response deterministic regardless of
                // what order the database happened to hand rows back in — device tokens are loaded
                // through the same Include as the Traveler row itself, so there is no separate
                // IQueryable to chain an ORDER BY onto the way GetAllMembershipsForTravelerAsync
                // does below.
                traveler.DeviceTokens.OrderBy(t => t.LastSeenUtc).Select(t => new AdminDeviceTokenResult(
                    t.Id,
                    t.Platform,
                    Mask<DeviceToken>(nameof(DeviceToken.Token), t.Token),
                    t.LastSeenUtc)).ToList(),
                trackedFlights.Select(x => new AdminTrackedFlightResult(
                    x.Tracking.Id, x.Flight.Id, x.Flight.FlightNumber, x.Flight.AirlineId,
                    x.Flight.DepartureAirportId, x.Flight.ArrivalAirportId, x.Flight.ServiceDateUtc, x.Tracking.Notify))
                    .ToList(),
                trackedFlightsTruncated,
                memberships.Select(m => new AdminMembershipResult(
                    m.Id,
                    m.ProgramId,
                    Mask<TravelerLoyaltyMembership>(nameof(TravelerLoyaltyMembership.AccountNumber), m.AccountNumber),
                    m.TierId,
                    m.TierExpiryDate)).ToList());
        }

        private static string Mask<TEntity>(string propertyName, string value) =>
            SensitiveProperties.IsSensitive(typeof(TEntity), propertyName)
                ? SensitiveProperties.RedactedPlaceholder
                : value;
    }
}
