namespace FlightAssistant.Application.Interfaces.Notifications
{
    public interface IPushNotificationService
    {
        /// <summary>
        /// Sends a push notification to the given device tokens.
        /// </summary>
        /// <returns>
        /// The device tokens that APNs reported as permanently invalid
        /// (e.g. BadDeviceToken, Unregistered, DeviceTokenNotForTopic). Callers
        /// should remove these tokens from storage. Empty when none were rejected.
        /// </returns>
        Task<IReadOnlyCollection<string>> SendAsync(IEnumerable<string> deviceTokens, string title, string body, CancellationToken ct = default);
    }
}
