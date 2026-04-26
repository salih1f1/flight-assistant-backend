using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.Domain.Travelers.Exceptions
{
    public sealed class DeviceTokenNotFoundException : DomainException
    {
        public DeviceTokenNotFoundException(Guid travelerId, Guid tokenId)
            : base($"Device token '{tokenId}' not found for traveler '{travelerId}'.", statusCode: 404) { }
    }
}
