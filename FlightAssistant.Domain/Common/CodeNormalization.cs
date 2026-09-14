// AdminVerifyEndpoints (#331) canonicalises the country-code route parameter with
// ToCanonicalCode() before lookup, the same helper Country.Create uses — hence API needing
// InternalsVisibleTo here.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FlightAssistant.API")]

namespace FlightAssistant.Domain.Common
{
    /// <summary>
    /// Helpers for normalizing standardized reference codes (IATA, ICAO, ISO country,
    /// alliance) to their canonical form. These codes are case-insensitive on input but
    /// stored uppercase so lookups and joins are deterministic.
    /// </summary>
    internal static class CodeNormalization
    {
        /// <summary>Trim and uppercase a required code.</summary>
        public static string ToCanonicalCode(this string value) =>
            value.Trim().ToUpperInvariant();

        /// <summary>Trim and uppercase an optional code; blank input becomes <c>null</c>.</summary>
        public static string? ToCanonicalCodeOrNull(this string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }
}
