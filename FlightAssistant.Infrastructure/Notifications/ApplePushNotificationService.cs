using dotAPNS;
using FlightAssistant.Application.Interfaces.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace FlightAssistant.Infrastructure.Notifications
{
    /// <summary>Sends push notifications to iOS devices via APNs (JWT-based auth).</summary>
    public sealed class ApplePushNotificationService : IPushNotificationService
    {
        private readonly ILogger<ApplePushNotificationService> _logger;
        private readonly Lazy<IApnsClient?> _client;
        private readonly ResiliencePipeline _pipeline;

        public ApplePushNotificationService(
            IOptions<ApnsOptions> options,
            ILogger<ApplePushNotificationService> logger)
        {
            _logger = logger;
            _client = new Lazy<IApnsClient?>(() => BuildClient(options.Value));
            _pipeline = BuildResiliencePipeline();
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
                var maskedToken = MaskToken(token);
                try
                {
                    await _pipeline.ExecuteAsync(async innerCt =>
                    {
                        var push = new ApplePush(ApplePushType.Alert)
                            .AddToken(token)
                            .AddAlert(title, body)
                            .AddBadge(1);

                        var response = await client.SendAsync(push, innerCt);
                        if (!response.IsSuccessful)
                        {
                            _logger.LogWarning("APNs push failed for {Token}: {Reason}",
                                maskedToken, response.ReasonString);
                            throw new ApnsDeliveryException(response.ReasonString ?? "Unknown APNs error");
                        }

                        _logger.LogDebug("APNs push sent to {Token}", maskedToken);
                    }, ct);
                }
                catch (BrokenCircuitException)
                {
                    _logger.LogError("APNs circuit breaker open — stopping sends for notification: {Title}", title);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "APNs push failed after retries for token {Token}", maskedToken);
                }
            }
        }

        private ResiliencePipeline BuildResiliencePipeline() =>
            new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    ShouldHandle = new PredicateBuilder().Handle<ApnsDeliveryException>()
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    // Open after 50% failure rate across at least 5 calls within a 30 s window
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(60),
                    ShouldHandle = new PredicateBuilder().Handle<ApnsDeliveryException>()
                })
                .Build();

        private static string MaskToken(string token) =>
            token.Length > 8 ? token[..8] + "..." : "***";

        private static IApnsClient? BuildClient(ApnsOptions opts)
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

    internal sealed class ApnsDeliveryException(string reason) : Exception(reason);
}
