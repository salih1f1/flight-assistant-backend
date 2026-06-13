using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace FlightAssistant.API.Telemetry
{
    /// <summary>
    /// Redacts the AviationStack <c>access_key</c> query parameter from dependency telemetry
    /// so the API key never reaches Application Insights.
    /// </summary>
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
            if (item is DependencyTelemetry dependency
                && dependency.Data is { } data
                && data.Contains("access_key=", StringComparison.OrdinalIgnoreCase))
            {
                dependency.Data = AccessKeyRegex.Replace(data, "access_key=***");
            }

            _next.Process(item);
        }
    }
}
