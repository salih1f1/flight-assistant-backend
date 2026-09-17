using System.ComponentModel.DataAnnotations;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroDataBox
{
    /// <summary>
    /// AeroDataBox via the RapidAPI marketplace (#422). <see cref="ApiKey"/> is optional — like
    /// <c>GitHubOptions.Token</c>, its absence must not stop the app starting; route search falls
    /// back to AeroAPI instead. It deliberately carries no validation attribute and is exempt from
    /// <c>ValidateOnStart</c> for that reason — every OTHER property here IS validated on start
    /// (#422 final review, finding 3): an invalid <see cref="TimeoutSeconds"/> used to throw only
    /// when a search first resolved the named <c>HttpClient</c>, taking down every
    /// <c>GET /flights/search</c> request — flight-number searches and the AeroAPI fallback for an
    /// unset key included — rather than failing loudly at startup.
    /// </summary>
    public sealed class AeroDataBoxOptions
    {
        public const string RapidApiHost = "aerodatabox.p.rapidapi.com";

        /// <summary>
        /// Fixed rather than configurable (#422 final review, finding 2/3), following
        /// <c>GitHubOptions.ApiBaseUrl</c>'s precedent: <c>X-RapidAPI-Host</c> is already
        /// hard-coded to <see cref="RapidApiHost"/>, so a different base URL could never have
        /// worked against RapidAPI anyway, and fixing it removes a misconfiguration surface (an
        /// empty or non-URL <c>BaseUrl</c> used to throw at first HttpClient resolution) rather
        /// than merely validating around it.
        /// </summary>
        public const string ApiBaseUrl = $"https://{RapidApiHost}/";

        public string? ApiKey { get; set; }

        [Range(1, 300)]
        public int TimeoutSeconds { get; set; } = 30;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
    }
}
