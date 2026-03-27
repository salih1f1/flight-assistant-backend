using dotAPNS;
using FlightAssistant.Application.Interfaces.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.Notifications
{
    /// <summary>Sends push notifications to iOS devices via APNs (JWT-based auth).</summary>
    public sealed class ApplePushNotificationService : IPushNotificationService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<ApplePushNotificationService> _logger;

        public ApplePushNotificationService(IConfiguration config, ILogger<ApplePushNotificationService> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task SendAsync(IEnumerable<string> deviceTokens, string title, string body, CancellationToken ct = default)
        {
            var bundleId = _config["Apns:BundleId"];
            var keyId = _config["Apns:KeyId"];
            var teamId = _config["Apns:TeamId"];
            var p8KeyPath = _config["Apns:P8KeyPath"];
            var useSandbox = _config.GetValue<bool>("Apns:UseSandbox", false);

            if (string.IsNullOrWhiteSpace(bundleId) || string.IsNullOrWhiteSpace(keyId)
                || string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(p8KeyPath))
            {
                _logger.LogWarning("APNs is not configured. Skipping push notification: {Title}", title);
                return;
            }

            var options = new ApnsJwtOptions
            {
                BundleId = bundleId,
                KeyId = keyId,
                TeamId = teamId,
                CertFilePath = p8KeyPath
            };

            var client = ApnsClient.CreateUsingJwt(new HttpClient(), options);

            foreach (var token in deviceTokens)
            {
                try
                {
                    var push = new ApplePush(ApplePushType.Alert)
                        .AddToken(token)
                        .AddAlert(title, body)
                        .AddBadge(1);

                    var response = await client.SendAsync(push, ct);
                    if (response.IsSuccessful)
                    {
                        _logger.LogDebug("APNs push sent to {Token}", token[..8] + "...");
                    }
                    else
                    {
                        _logger.LogWarning("APNs push failed for {Token}: {Reason}", token[..8] + "...", response.ReasonString);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "APNs push exception for token {Token}", token[..8] + "...");
                }
            }
        }
    }
}
