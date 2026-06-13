using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace FlightAssistant.API.Telemetry
{
    /// <summary>
    /// Redacts the AviationStack <c>access_key</c> query parameter from telemetry before it
    /// reaches Application Insights, so the API key never leaks.
    /// </summary>
    /// <remarks>
    /// Covers <see cref="DependencyTelemetry.Data"/> for AviationStack HTTP dependency calls, as
    /// well as Polly/<c>Microsoft.Extensions.Http.Resilience</c> retry/timeout/circuit-breaker
    /// telemetry that is surfaced via Serilog as <see cref="TraceTelemetry"/> and
    /// <see cref="ExceptionTelemetry"/> — these can embed the full request URI, including
    /// the <c>access_key</c> query parameter, in the rendered <c>Message</c> or in structured
    /// logging properties (see #227).
    /// </remarks>
    public sealed class SanitizeAviationStackTelemetryProcessor : ITelemetryProcessor
    {
        private static readonly Regex AccessKeyRegex = new(
            "access_key=[^&]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ITelemetryProcessor _next;

        public SanitizeAviationStackTelemetryProcessor(ITelemetryProcessor next)
        {
            _next = next;
        }

        public void Process(ITelemetry item)
        {
            if (item is DependencyTelemetry dependency)
            {
                dependency.Data = RedactAccessKey(dependency.Data) ?? dependency.Data;
            }

            if (item is TraceTelemetry trace)
            {
                trace.Message = RedactAccessKey(trace.Message) ?? trace.Message;
            }

            if (item is ISupportProperties supportsProperties)
            {
                foreach (var key in supportsProperties.Properties.Keys.ToList())
                {
                    var value = supportsProperties.Properties[key];
                    supportsProperties.Properties[key] = RedactAccessKey(value) ?? value;
                }
            }

            _next.Process(item);
        }

        private static string? RedactAccessKey(string? value) =>
            value is not null && value.Contains("access_key=", StringComparison.OrdinalIgnoreCase)
                ? AccessKeyRegex.Replace(value, "access_key=***")
                : value;
    }
}
