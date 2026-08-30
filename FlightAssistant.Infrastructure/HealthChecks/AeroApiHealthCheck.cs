using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.HealthChecks
{
    public sealed class AeroApiHealthCheck : IHealthCheck
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _baseUrl;

        public AeroApiHealthCheck(IHttpClientFactory httpClientFactory, IOptions<AeroApiOptions> options)
        {
            _httpClientFactory = httpClientFactory;
            _baseUrl = options.Value.BaseUrl;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken ct = default)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                // A HEAD request to the base URL checks connectivity without consuming API quota
                var response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Head, _baseUrl), ct);

                // Any HTTP response (including 401/403) means the host is reachable
                return HealthCheckResult.Healthy($"Reachable — HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Degraded("Host unreachable", ex);
            }
        }
    }
}
