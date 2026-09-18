using System.Net;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>
    /// Records the outcome of every call through one named <see cref="HttpClient"/>.
    ///
    /// <para>
    /// A <see cref="DelegatingHandler"/> is the cheapest correct seam: it sees every call through
    /// the client, including calls in code not yet written, and it cannot be forgotten at a new
    /// call site the way an explicit <c>Record</c> can. That is the failure this whole feature
    /// exists to prevent.
    /// </para>
    ///
    /// <para>
    /// <b>It never changes the call it observes.</b> A transport failure is recorded and then
    /// rethrown unchanged; an observer that throws is swallowed. Diagnostics must not alter or
    /// break the thing being diagnosed.
    /// </para>
    /// </summary>
    public sealed class DataSourceObservingHandler : DelegatingHandler
    {
        private readonly IDataSourceObserver _observer;
        private readonly TimeProvider _timeProvider;

        public DataSourceObservingHandler(IDataSourceObserver observer, TimeProvider timeProvider)
        {
            _observer = observer;
            _timeProvider = timeProvider;
        }

        /// <summary>Set by the DI registration, one instance per named client.</summary>
        public string SourceKey { get; set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                var response = await base.SendAsync(request, ct);
                Record(Map(response.StatusCode), Describe(response.StatusCode));
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The category only. An upstream message can name an internal address — see the
                // same rule on VerificationSourceDownloader.
                Record(DataSourceOutcome.Failed, $"{SourceKey} could not be reached (network error)");
                throw;
            }
        }

        private static DataSourceOutcome Map(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DataSourceOutcome.Unauthorized,
            HttpStatusCode.TooManyRequests => DataSourceOutcome.RateLimited,
            _ when (int)status >= 200 && (int)status < 400 => DataSourceOutcome.Ok,
            _ => DataSourceOutcome.Failed,
        };

        // Null on success: there is nothing to explain, and a stored "detail" on a healthy row
        // would be noise the panel has to filter out.
        private string? Describe(HttpStatusCode status) =>
            Map(status) == DataSourceOutcome.Ok ? null : $"{SourceKey} returned HTTP {(int)status}";

        private void Record(DataSourceOutcome outcome, string? detail)
        {
            try
            {
                _observer.Record(SourceKey, outcome, _timeProvider.GetUtcNow(), detail);
            }
            catch
            {
                // Never break the call being observed.
            }
        }
    }
}
