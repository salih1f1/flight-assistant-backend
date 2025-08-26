using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Travelers.Entities
{
    public class DeviceToken : BaseEntity
    {
        public Guid TravelerId { get; private set; }
        public string Platform { get; private set; }
        public string Token { get; private set; }
        public DateTime LastSeenUtc { get; private set; }

        private DeviceToken() { } // EF

        internal DeviceToken(Guid travelerId, string platform, string token, DateTime lastSeenUtc)
        {
            TravelerId = travelerId;
            Platform = platform;
            Token = token;
            LastSeenUtc = lastSeenUtc;
        }

        internal void Touch(DateTime seenUtc) => LastSeenUtc = seenUtc;
    }
}
