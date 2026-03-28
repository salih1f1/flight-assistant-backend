namespace FlightAssistant.Application.Interfaces.Notifications
{
    public interface IPushNotificationService
    {
        Task SendAsync(IEnumerable<string> deviceTokens, string title, string body, CancellationToken ct = default);
    }
}
