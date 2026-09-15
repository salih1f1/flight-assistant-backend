using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// Reads a <see cref="VerificationEntityType"/> from an admin request (#378): exactly one of the
    /// member names, compared ordinally. <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> is not
    /// used because it also accepts numbers (<c>"0"</c>), surrounding whitespace and, with
    /// <c>ignoreCase</c>, any casing, none of which the endpoints accept.
    /// </summary>
    public static class VerificationEntityTypeNames
    {
        /// <summary>The accepted names, for validation messages: <c>Airport, Airline, Country</c>.</summary>
        public static string Allowed { get; } = string.Join(", ", Enum.GetNames<VerificationEntityType>());

        /// <summary>True when <paramref name="value"/> is exactly a member name.</summary>
        public static bool TryParse(string? value, out VerificationEntityType entityType)
        {
            foreach (var candidate in Enum.GetValues<VerificationEntityType>())
            {
                if (string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
                {
                    entityType = candidate;
                    return true;
                }
            }

            entityType = default;
            return false;
        }
    }
}
