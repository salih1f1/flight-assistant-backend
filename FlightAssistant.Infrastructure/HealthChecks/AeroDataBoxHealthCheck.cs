using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlightAssistant.Infrastructure.HealthChecks
{
    public sealed class AeroDataBoxHealthCheck : IHealthCheck
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _baseUrl;

        public AeroDataBoxHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration config)
        {
            _httpClientFactory = httpClientFactory;
            _baseUrl = config["AeroDataBox:BaseUrl"] ?? "https://aerodatabox.p.rapidapi.com/";
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
