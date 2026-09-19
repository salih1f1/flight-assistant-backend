using System.Globalization;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroDataBox
{
    /// <summary>
    /// Records AeroDataBox's own quota headers off every response through its named client.
    ///
    /// <para>
    /// A <see cref="DelegatingHandler"/> for the reason <c>DataSourceObservingHandler</c> gives: it
    /// sees every call including ones in code not yet written, and cannot be forgotten at a new call
    /// site. It costs no extra request — these headers ride on traffic already being sent.
    /// </para>
    ///
    /// <para><b>It never changes the call it observes.</b> A transport failure is rethrown unchanged
    /// and nothing is recorded, because a failed call reports no quota.</para>
    /// </summary>
    public sealed class AeroDataBoxQuotaHandler : DelegatingHandler
    {
        public const string RemainingHeader = "x-ratelimit-requests-remaining";
        public const string LimitHeader = "x-ratelimit-requests-limit";

        private readonly AeroDataBoxQuotaHolder _holder;
        private readonly TimeProvider _time;

        public AeroDataBoxQuotaHandler(AeroDataBoxQuotaHolder holder, TimeProvider time)
        {
            _holder = holder;
            _time = time;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);

            try
            {
                _holder.Record(ReadInt(response, RemainingHeader), ReadInt(response, LimitHeader),
                    _time.GetUtcNow());
            }
            catch
            {
                // Diagnostics must not break the thing being diagnosed.
            }

            return response;
        }

        private static int? ReadInt(HttpResponseMessage response, string header) =>
            response.Headers.TryGetValues(header, out var values) &&
            int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }
}
