using System.Net;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;

namespace FlightAssistant.Infrastructure.DataSources.Probes
{
    /// <summary>
    /// The shape every probe that speaks HTTP shares: send one request on the source's own named
    /// client and read the status code. Going through the named client is what makes
    /// <see cref="DataSourceObservingHandler"/> record the observation, so these probes deliberately
    /// record nothing themselves — see <see cref="IDataSourceProbe"/>.
    /// </summary>
    public abstract class HttpDataSourceProbe : IDataSourceProbe
    {
        private readonly IHttpClientFactory _httpClientFactory;

        protected HttpDataSourceProbe(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

        public abstract string Key { get; }

        /// <summary>The named client to send on — the same one the application's real calls use.</summary>
        protected abstract string HttpClientName { get; }

        protected abstract HttpRequestMessage BuildRequest();

        public async Task<DataSourceOutcome> ProbeAsync(CancellationToken ct)
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = BuildRequest();

            try
            {
                using var response = await client.SendAsync(request, ct);
                return Map(response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Unreachable is an answer, not an error — and the handler has already recorded it.
                return DataSourceOutcome.Failed;
            }
        }

        /// <summary>The same mapping <see cref="DataSourceObservingHandler"/> applies, so a probe's
        /// answer and the observation it produces can never disagree.</summary>
        protected static DataSourceOutcome Map(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DataSourceOutcome.Unauthorized,
            HttpStatusCode.TooManyRequests => DataSourceOutcome.RateLimited,
            _ when (int)status >= 200 && (int)status < 400 => DataSourceOutcome.Ok,
            _ => DataSourceOutcome.Failed,
        };
    }
}
