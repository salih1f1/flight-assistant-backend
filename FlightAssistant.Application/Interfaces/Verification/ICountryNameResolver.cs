namespace FlightAssistant.Application.Interfaces.Verification
{
    /// <summary>
    /// One country-name lookup's answer. Shaped like <see cref="VerificationSourceFetchResult"/>:
    /// a miss and a failure are different outcomes, because a failed dataset load must never be
    /// reported as "no such country" -- that would turn a transient outage into a 404 for an
    /// airline that genuinely exists (#465).
    /// </summary>
    public sealed record CountryCodeResolution(string? Code, string? Error)
    {
        public bool IsError => Error is not null;

        public static readonly CountryCodeResolution NotFound = new(null, null);

        public static CountryCodeResolution Found(string code)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            return new CountryCodeResolution(code, null);
        }

        public static CountryCodeResolution Failed(string error)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(error);
            return new CountryCodeResolution(null, error);
        }
    }

    /// <summary>
    /// Resolves a country's <i>name</i> to its two-letter code (#465). Needed because
    /// <c>aeroapi</c> reports an operator's country as a name ("United Kingdom") and never a code,
    /// while <c>Airline.CountryCode</c> is a required foreign key into <c>countries</c>.
    /// Implemented by <c>MledozeCountriesVerificationSource</c>, which already holds
    /// <c>name.common</c> -> <c>cca2</c> for every country: this is a reverse index over the
    /// dataset that source downloads anyway, not a second download.
    /// </summary>
    public interface ICountryNameResolver
    {
        /// <summary>
        /// The two-letter code for <paramref name="countryName"/>, matched with the same
        /// accent/case/punctuation-insensitive rule <c>VerificationComparer</c> applies to Text
        /// columns. A blank name returns <see cref="CountryCodeResolution.NotFound"/> without
        /// loading the dataset.
        /// </summary>
        Task<CountryCodeResolution> TryResolveCodeByNameAsync(string? countryName, CancellationToken ct = default);
    }
}
