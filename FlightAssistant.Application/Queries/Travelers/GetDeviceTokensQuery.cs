using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Travelers
{
    public record DeviceTokenResult(Guid Id, string Platform, string Token, DateTime LastSeenUtc);

    public record GetDeviceTokensQuery(Guid TravelerId) : IRequest<IReadOnlyList<DeviceTokenResult>>;

    public sealed class GetDeviceTokensQueryHandler : IRequestHandler<GetDeviceTokensQuery, IReadOnlyList<DeviceTokenResult>>
    {
        private readonly ITravelerRepository _travelers;

        public GetDeviceTokensQueryHandler(ITravelerRepository travelers) => _travelers = travelers;

        public async Task<IReadOnlyList<DeviceTokenResult>> Handle(GetDeviceTokensQuery query, CancellationToken ct)
        {
            var traveler = await _travelers.GetByIdAsync(query.TravelerId, ct);
            if (traveler is null) return [];

            return traveler.DeviceTokens
                .Select(d => new DeviceTokenResult(d.Id, d.Platform, d.Token, d.LastSeenUtc))
                .ToList();
        }
    }
}
