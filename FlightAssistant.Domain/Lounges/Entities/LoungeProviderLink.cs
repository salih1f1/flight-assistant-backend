using FlightAssistant.Domain.Lounges.Enums;

namespace FlightAssistant.Domain.Lounges.Entities
{
    public sealed class LoungeProviderLink
    {
        public int LoungeId { get; private set; }
        public int ProviderId { get; private set; }
        public LoungeProviderRole Role { get; private set; }
        public string? Notes { get; private set; }
        public string? MinAllianceTierCode { get; private set; }

        public Lounge Lounge { get; private set; } = default!;
        public LoungeProvider Provider { get; private set; } = default!;

        public static LoungeProviderLink Create(
            int loungeId, int providerId, LoungeProviderRole role,
            string? notes = null, string? minAllianceTierCode = null) =>
            new()
            {
                LoungeId = loungeId,
                ProviderId = providerId,
                Role = role,
                Notes = notes,
                MinAllianceTierCode = minAllianceTierCode
            };
    }
}
