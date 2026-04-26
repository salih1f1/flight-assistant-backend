using dotAPNS;
using FlightAssistant.Application.Interfaces.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Notifications
{
    /// <summary>Sends push notifications to iOS devices via APNs (JWT-based auth).</summary>
    public sealed class ApplePushNotificationService : IPushNotificationService
    {
        private readonly ILogger<ApplePushNotificationService> _logger;
        private readonly Lazy<IApnsClient?> _client;

        public ApplePushNotificationService(
            IOptions<ApnsOptions> options,
            ILogger<ApplePushNotificationService> logger)
        {
            _logger = logger;
            _client = new Lazy<IApnsClient?>(() => BuildClient(options.Value));
        }

        public async Task SendAsync(IEnumerable<string> deviceTokens, string title, string body, CancellationToken ct = default)
        {
            var client = _client.Value;
            if (client is null)
            {
                _logger.LogWarning("APNs is not configured. Skipping push notification: {Title}", title);
                return;
            }

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
                        _logger.LogDebug("APNs push sent to {Token}", token[..8] + "...");
                    else
                        _logger.LogWarning("APNs push failed for {Token}: {Reason}", token[..8] + "...", response.ReasonString);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "APNs push exception for token {Token}", token[..8] + "...");
                }
            }
        }

        private IApnsClient? BuildClient(ApnsOptions opts)
        {
            if (string.IsNullOrWhiteSpace(opts.BundleId) || string.IsNullOrWhiteSpace(opts.KeyId)
                || string.IsNullOrWhiteSpace(opts.TeamId) || string.IsNullOrWhiteSpace(opts.P8KeyContent))
                return null;

            var jwtOptions = new ApnsJwtOptions
            {
                BundleId = opts.BundleId,
                KeyId = opts.KeyId,
                TeamId = opts.TeamId,
                CertContent = opts.P8KeyContent
            };

            return ApnsClient.CreateUsingJwt(new HttpClient(), jwtOptions);
        }
    }
}
