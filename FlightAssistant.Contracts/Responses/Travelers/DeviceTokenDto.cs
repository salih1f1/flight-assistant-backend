namespace FlightAssistant.Contracts.Responses.Travelers
{
    public record DeviceTokenDto(Guid Id, string Platform, string Token, DateTime LastSeenUtc);
}
