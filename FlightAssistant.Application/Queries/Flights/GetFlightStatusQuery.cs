using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.Application.Queries.Flights
{
    public record FlightStatusEventResult(
        long Id,
        string StatusCode,
        string StatusName,
        DateTime EffectiveAtUtc,
        string Source,
        string? Gate,
        string? Terminal,
        string? BaggageClaim,
        string? Remarks
    );

    public record GetFlightStatusQuery(Guid TravelerId, int FlightId) : IRequest<IReadOnlyList<FlightStatusEventResult>>;

    public sealed class GetFlightStatusQueryHandler : IRequestHandler<GetFlightStatusQuery, IReadOnlyList<FlightStatusEventResult>>
    {
        private readonly IFlightRepository _flights;

        public GetFlightStatusQueryHandler(IFlightRepository flights) => _flights = flights;

        public async Task<IReadOnlyList<FlightStatusEventResult>> Handle(GetFlightStatusQuery query, CancellationToken ct)
        {
            var link = await _flights.GetTravelerFlightAsync(query.TravelerId, query.FlightId, ct);
            if (link is null)
                throw new DomainException("Not tracking this flight.", 403);

            var events = await _flights.GetFlightStatusEventsAsync(query.FlightId, ct);
            return events.Select(e => new FlightStatusEventResult(
                e.Id,
                e.Status.Code,
                e.Status.Name,
                e.EffectiveAtUtc,
                e.Source,
                e.Gate,
                e.Terminal,
                e.BaggageClaim,
                e.Remarks
            )).ToList();
        }
    }
}
