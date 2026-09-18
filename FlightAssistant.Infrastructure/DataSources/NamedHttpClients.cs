using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using FlightAssistant.Infrastructure.ExternalServices.Verification;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>
    /// Every named <see cref="HttpClient"/> this application registers. Kept beside the catalog so
    /// <c>DataSourceCatalogTests.EveryNamedHttpClientHasACatalogEntry</c> can assert the two agree:
    /// adding a client here without a catalog entry fails the build, which is what stops a new
    /// outside dependency from being invisible on the health page.
    ///
    /// <para>
    /// Each entry names the constant the registration itself uses, rather than repeating the
    /// string: a second constant holding the same literal is a second thing to keep in step.
    /// </para>
    /// </summary>
    public static class NamedHttpClients
    {
        public static readonly IReadOnlyList<string> All =
        [
            AeroApiVerificationSource.HttpClientName,
            AeroDataBoxFlightSearchProvider.HttpClientName,
            GitHubCiRunProvider.ApiClientName,
            GitHubCiRunProvider.DownloadClientName,
            OurAirportsVerificationSource.HttpClientName,
            MledozeCountriesVerificationSource.HttpClientName,
        ];
    }
}
