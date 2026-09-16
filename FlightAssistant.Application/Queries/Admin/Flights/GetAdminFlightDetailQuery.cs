using System.Text.Json;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Flights
{
    /// <summary>
    /// One row of a flight's status-event history on the admin drill-down (#322).
    /// <c>StatusCode</c>/<c>StatusName</c> are this system's own resolved status
    /// (<see cref="FlightStatus.Code"/>/<see cref="FlightStatus.Name"/>); <c>ProviderRawStatus</c>
    /// is the provider's own status text as recorded at sync time — see
    /// <see cref="GetAdminFlightDetailQueryHandler.ExtractProviderRawStatus"/> for where it comes
    /// from and why it is null for some rows.
    /// </summary>
    public sealed record AdminFlightStatusEventResult(
        long Id,
        string StatusCode,
        string StatusName,
        string? ProviderRawStatus,
        DateTime EffectiveAtUtc,
        DateTime RecordedAtUtc,
        string Source);

    /// <summary>
    /// The flight with its current status and recent status-event history, most recently recorded
    /// first (by id, #351 — not by effective time, which can move backwards). Backs
    /// <c>GET /admin/flights/{id}/detail</c>. <c>CurrentStatus</c> is simply
    /// <c>StatusHistory[0]</c> (null when the flight has no recorded events yet) — a flight has no
    /// separate "current status" column of its own; it is always derived from the most recently
    /// recorded event (by event id).
    /// <c>StatusHistoryTruncated</c> is true when <see cref="StatusHistory"/> was cut off at
    /// <see cref="GetAdminFlightDetailQueryHandler.MaxStatusHistory"/> — without it, a response with
    /// exactly 50 events is indistinguishable from "this flight had exactly 50 events", and a caller
    /// has no signal to fall back to <c>GET /admin/data/flight-status-events</c> (A4) for the rest.
    /// </summary>
    public sealed record AdminFlightDetailResult(
        int Id,
        int AirlineId,
        string FlightNumber,
        int DepartureAirportId,
        int ArrivalAirportId,
        DateOnly ServiceDate,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc,
        DateTime? EstimatedDepartureUtc,
        DateTime? EstimatedArrivalUtc,
        DateTime? ActualDepartureUtc,
        DateTime? ActualArrivalUtc,
        string? EquipmentAircraftIcao,
        AdminFlightStatusEventResult? CurrentStatus,
        IReadOnlyList<AdminFlightStatusEventResult> StatusHistory,
        bool StatusHistoryTruncated);

    public sealed record GetAdminFlightDetailQuery(int Id) : IRequest<AdminFlightDetailResult?>;

    /// <summary>
    /// Assembles the flight drill-down from two repositories — <see cref="IAdminFlightRepository"/>
    /// for the flight row (one query) and <see cref="IFlightRepository"/> for its recent status
    /// events (one query, capped server-side) — never a query per event.
    /// </summary>
    public sealed class GetAdminFlightDetailQueryHandler : IRequestHandler<GetAdminFlightDetailQuery, AdminFlightDetailResult?>
    {
        /// <summary>
        /// The status-event history this endpoint returns is capped, not paged as its own route
        /// (#322 offers either; this endpoint takes the simpler of the two): a flight tracked for
        /// weeks, polled every couple of minutes by <c>FlightStatusSyncBackgroundService</c>,
        /// accumulates <see cref="FlightStatusEvent"/> rows without bound, and this "one call"
        /// detail view must not load all of them. 50 matches <c>PagedRequest</c>'s own default
        /// <c>PageSize</c> elsewhere in this API — a deliberate, documented limit, not an arbitrary
        /// one, and comfortably enough "what the sync actually saw" for an operator to read.
        /// </summary>
        public const int MaxStatusHistory = 50;

        private readonly IAdminFlightRepository _adminFlights;
        private readonly IFlightRepository _flights;

        public GetAdminFlightDetailQueryHandler(IAdminFlightRepository adminFlights, IFlightRepository flights)
        {
            _adminFlights = adminFlights;
            _flights = flights;
        }

        public async Task<AdminFlightDetailResult?> Handle(GetAdminFlightDetailQuery request, CancellationToken ct)
        {
            var flight = await _adminFlights.GetDetailAsync(request.Id, ct);
            if (flight is null) return null;

            // Fetch one more than the cap so "truncated" is exact rather than an inference.
            // Comparing Count == MaxStatusHistory would report a flight with precisely
            // MaxStatusHistory events as truncated when it is complete, sending an operator to the
            // generic browser for rows that are not there.
            var events = await _flights.GetRecentFlightStatusEventsAsync(request.Id, MaxStatusHistory + 1, ct);
            var truncated = events.Count > MaxStatusHistory;
            var history = events.Take(MaxStatusHistory).Select(ToResult).ToList();

            return new AdminFlightDetailResult(
                flight.Id,
                flight.AirlineId,
                flight.FlightNumber,
                flight.DepartureAirportId,
                flight.ArrivalAirportId,
                flight.ServiceDateUtc,
                flight.ScheduledDepartureUtc,
                flight.ScheduledArrivalUtc,
                flight.EstimatedDepartureUtc,
                flight.EstimatedArrivalUtc,
                flight.ActualDepartureUtc,
                flight.ActualArrivalUtc,
                flight.EquipmentAircraftIcao,
                history.Count > 0 ? history[0] : null,
                history,
                truncated);
        }

        private static AdminFlightStatusEventResult ToResult(FlightStatusEvent e) => new(
            e.Id,
            e.Status.Code,
            e.Status.Name,
            ExtractProviderRawStatus(e.RawPayloadJson),
            e.EffectiveAtUtc,
            e.RecordedAtUtc,
            e.Source);

        /// <summary>
        /// AeroApiFlightStatusProvider stores AeroAPI's whole raw flight row as
        /// <see cref="FlightStatusEvent.RawPayloadJson"/> (see CLAUDE.md's "Status is prose, not a
        /// code" — <c>AeroApiFlightStatusProvider</c>), which is where the provider's own status
        /// text (e.g. <c>"Scheduled"</c>, <c>"En Route / On Time"</c>) actually lives — it is never
        /// carried as its own column, only ever as part of that raw payload. Returns null — "where
        /// recorded" — for a row with no raw payload at all (a manually-created event, whose
        /// <c>Source</c> is <c>"manual"</c>) or one whose payload turns out not to carry a
        /// <c>status</c> string; malformed JSON degrades the same way rather than throwing, since
        /// this is a display convenience for an operator, not load-bearing data.
        /// </summary>
        private static string? ExtractProviderRawStatus(string? rawPayloadJson)
        {
            if (string.IsNullOrWhiteSpace(rawPayloadJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(rawPayloadJson);
                return doc.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                    ? status.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
