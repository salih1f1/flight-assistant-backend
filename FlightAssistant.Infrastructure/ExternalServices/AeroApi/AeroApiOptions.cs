using System.ComponentModel.DataAnnotations;
using FlightAssistant.Application.Interfaces.ExternalServices;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Configuration for the FlightAware AeroAPI client, bound from the <c>AeroApi</c> section.
    /// Registered with <c>ValidateDataAnnotations().ValidateOnStart()</c> so a missing/invalid
    /// value fails fast at startup rather than surfacing as a confusing runtime error on the
    /// first search. Implements <see cref="IFlightSearchHorizonOptions"/> so the Application-layer
    /// query validator can read the real configured horizon without this (Infrastructure) project
    /// being referenced from Application — see the DI registration in
    /// <c>FlightAssistant.Infrastructure.DependencyInjection</c>.
    /// </summary>
    public sealed class AeroApiOptions : IFlightSearchHorizonOptions
    {
        /// <summary>Sent as the <c>x-apikey</c> header on every request. Never logged.</summary>
        [Required]
        [MinLength(1)]
        public string ApiKey { get; set; } = string.Empty;

        public string BaseUrl { get; set; } = "https://aeroapi.flightaware.com/aeroapi/";

        /// <summary>Caps billable page fetches per search — a runaway paging loop is a billing risk
        /// on a metered tier.</summary>
        [Range(1, 20)]
        public int MaxPages { get; set; } = 4;

        /// <summary>Minimum spacing between outbound requests, enforced globally by
        /// <see cref="AeroApiThrottleGate"/>. The personal tier allows ~5 queries/minute.</summary>
        public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(12);

        [Range(1, 12)]
        public int MaxFutureMonths { get; set; } = 12;

        [Range(1, 3)]
        public int MaxPastMonths { get; set; } = 3;

        public int TimeoutSeconds { get; set; } = 30;
    }
}
