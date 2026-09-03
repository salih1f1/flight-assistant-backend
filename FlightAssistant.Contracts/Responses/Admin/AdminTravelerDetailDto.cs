namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One device token on the traveler detail drill-down (#322). <c>Token</c> arrives
    /// masked as <c>"&lt;redacted&gt;"</c> — reveal the real value via
    /// <c>POST /admin/data/{table}/{id}/reveal</c>.</summary>
    public record AdminDeviceTokenDto(Guid Id, string Platform, string Token, DateTime LastSeenUtc);

    /// <summary>One flight the traveler tracks, joined with its flight row.</summary>
    public record AdminTrackedFlightDto(
        Guid TrackingId,
        int FlightId,
        string FlightNumber,
        int AirlineId,
        DateOnly ServiceDate,
        bool Notify);

    /// <summary>One loyalty membership on the traveler detail drill-down. <c>AccountNumber</c>
    /// arrives masked as <c>"&lt;redacted&gt;"</c> — reveal the real value via
    /// <c>POST /admin/data/{table}/{id}/reveal</c>.</summary>
    public record AdminMembershipDto(
        Guid Id,
        int ProgramId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate,
        DateOnly? PointsExpiryDate);

    /// <summary>The traveler with its device tokens, tracked flights and loyalty memberships in one
    /// response. Backs <c>GET /admin/travelers/{id}/detail</c>. <c>Email</c> arrives masked as
    /// <c>"&lt;redacted&gt;"</c> — stricter than <c>GET /admin/travelers</c>, which does not mask it
    /// (#321).</summary>
    public record AdminTravelerDetailDto(
        Guid Id,
        string Email,
        string? DisplayName,
        DateTime CreatedUtc,
        IReadOnlyList<AdminDeviceTokenDto> DeviceTokens,
        IReadOnlyList<AdminTrackedFlightDto> TrackedFlights,
        IReadOnlyList<AdminMembershipDto> LoyaltyMemberships);
}
