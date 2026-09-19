using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using FlightAssistant.Application.Hosting;
using FlightAssistant.Application.Interfaces.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.Azure
{
    /// <summary>
    /// Reads the App Service the API is hosted in, straight from Azure Resource Manager -- the
    /// concrete <see cref="IAppServiceProvider"/> behind the App Service health section (#530).
    ///
    /// <para>
    /// <b>Why ARM, not a local check.</b> <c>alwaysOn</c> is a platform setting the process cannot
    /// observe from inside itself -- only Azure can say what it configured. The one GET that
    /// returns <c>state</c>/<c>availabilityState</c>/<c>enabled</c> also returns
    /// <c>siteConfig.alwaysOn</c> inline (verified against the live resource), so a single call
    /// answers everything this provider reports; no separate <c>/config/web</c> request.
    /// </para>
    ///
    /// <para>
    /// <b>Not running on App Service is not a fault.</b> A developer machine has none of
    /// <c>WEBSITE_SITE_NAME</c>, <c>WEBSITE_RESOURCE_GROUP</c>, or a subscription id --
    /// <see cref="AzureSubscription.FromWebsiteOwnerName"/> resolves that third one from
    /// <c>WEBSITE_OWNER_NAME</c>, the same source <see cref="AzureCostProvider"/>'s
    /// <c>WEBSITE_OWNER_NAME</c> fallback uses (#523), rather than the <c>AzureCost:SubscriptionId</c>
    /// override: that setting exists to point the cost panel at a subscription other than the one
    /// hosting the API, which is never what this provider needs -- the site it reads is always the
    /// one running this process. Missing any of the three answers
    /// <see cref="AppServiceSnapshot.NotConfigured"/> with no HTTP call: calling anyway would log a
    /// confusing failure for a state that is entirely expected, on every local run.
    /// </para>
    ///
    /// <para>
    /// <b>A refusal must report no facts at all.</b> 401/403 maps to
    /// <see cref="AppServiceSnapshot.NotAuthorized"/>, which carries every field as null -- never a
    /// half-filled snapshot with, say, <c>AlwaysOn: false</c>. A denied request never learned what
    /// Always On is; reporting false there would put an unearned warning on the panel. Same
    /// reasoning <see cref="AzureCostProvider"/> applies to its own NotAuthorized mapping.
    /// </para>
    /// </summary>
    public sealed class AppServiceProvider : IAppServiceProvider
    {
        /// <summary>The named <see cref="HttpClient"/> this provider resolves through --
        /// see <c>DependencyInjection.AddInfrastructure</c>.</summary>
        public const string HttpClientName = "AppService";

        private const string ApiVersion = "2023-12-01";

        private static readonly TokenRequestContext ManagementScope = new(["https://management.azure.com/.default"]);

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly TokenCredential _credential;
        private readonly ILogger<AppServiceProvider> _logger;

        public AppServiceProvider(
            HttpClient httpClient,
            IConfiguration configuration,
            TokenCredential credential,
            ILogger<AppServiceProvider> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _credential = credential;
            _logger = logger;
        }

        /// <summary>
        /// See <see cref="IAppServiceProvider"/> -- never throws for an expected condition. State
        /// says whether the resource could be READ; SiteState says what the read found. The two
        /// must never collapse into one: a stopped site read successfully is
        /// State=Ok/SiteState=Stopped, not Unavailable, because "we could not ask" and "the answer
        /// was no" are different facts the panel has to tell apart.
        /// </summary>
        public async Task<AppServiceSnapshot> GetStatusAsync(CancellationToken ct)
        {
            var siteName = _configuration["WEBSITE_SITE_NAME"];
            var resourceGroup = _configuration["WEBSITE_RESOURCE_GROUP"];
            var subscriptionId = AzureSubscription.FromWebsiteOwnerName(_configuration["WEBSITE_OWNER_NAME"]);

            if (string.IsNullOrWhiteSpace(siteName) || string.IsNullOrWhiteSpace(resourceGroup) || string.IsNullOrWhiteSpace(subscriptionId))
                return AppServiceSnapshot.NotConfigured();

            try
            {
                using var doc = await SendAsync(siteName, resourceGroup, subscriptionId, ct).ConfigureAwait(false);
                return MapSite(doc.RootElement);
            }
            catch (AppServiceHttpException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "Azure Resource Manager refused the App Service request with HTTP {StatusCode} -- the managed " +
                    "identity likely lacks Reader on the resource group.", (int)ex.StatusCode);
                return AppServiceSnapshot.NotAuthorized();
            }
            catch (AppServiceHttpException ex)
            {
                _logger.LogWarning("Azure Resource Manager returned HTTP {StatusCode} for the App Service.", (int)ex.StatusCode);
                return AppServiceSnapshot.Unavailable($"Azure Resource Manager returned HTTP {(int)ex.StatusCode}.");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Azure Resource Manager returned a response that could not be parsed.");
                return AppServiceSnapshot.Unavailable("Azure Resource Manager returned a response that could not be read.");
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // The deadline elapsed, not the caller -- genuine caller cancellation (guarded by
                // the `when`) is left to propagate rather than being reported as a site state, the
                // same distinction AzureCostProvider and VerificationSourceDownloader draw.
                _logger.LogWarning(ex, "Azure Resource Manager call timed out.");
                return AppServiceSnapshot.Unavailable("Timed out calling Azure Resource Manager.");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Azure Resource Manager call failed (network error).");
                return AppServiceSnapshot.Unavailable("Could not reach Azure Resource Manager (network error).");
            }
            catch (AuthenticationFailedException ex)
            {
                // DefaultAzureCredential could not obtain a token at all -- e.g. no managed identity
                // reachable locally. Distinct from a 401/403 FROM Azure: this never reached Azure.
                _logger.LogWarning(ex, "Could not authenticate to Azure for Resource Manager.");
                return AppServiceSnapshot.Unavailable("Could not authenticate to Azure.");
            }
        }

        /// <summary>
        /// Every field read with <see cref="JsonElement.TryGetProperty"/>; absent means null, never
        /// a substituted default. <see cref="AppServiceSnapshot.AlwaysOn"/> is the one this matters
        /// most for: when <c>siteConfig</c> is missing it must stay null, not become false, because
        /// unknown and confirmed-off are different facts the panel acts on differently.
        /// </summary>
        private static AppServiceSnapshot MapSite(JsonElement root)
        {
            var name = ReadString(root, "name");
            var hasProperties = root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;

            var state = hasProperties ? ReadString(properties, "state") : null;
            var availabilityState = hasProperties ? ReadString(properties, "availabilityState") : null;
            var enabled = hasProperties ? ReadBool(properties, "enabled") : null;

            bool? alwaysOn = null;
            int? instanceCount = null;
            if (hasProperties && properties.TryGetProperty("siteConfig", out var siteConfig) && siteConfig.ValueKind == JsonValueKind.Object)
            {
                alwaysOn = ReadBool(siteConfig, "alwaysOn");
                instanceCount = ReadInt(siteConfig, "numberOfWorkers");
            }

            return new AppServiceSnapshot(AppServiceState.Ok, name, state, availabilityState, enabled, alwaysOn, instanceCount, null);
        }

        private static string? ReadString(JsonElement obj, string property) =>
            obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool? ReadBool(JsonElement obj, string property) =>
            obj.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

        private static int? ReadInt(JsonElement obj, string property) =>
            obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number
                : null;

        /// <summary>
        /// Sends one request with a freshly-acquired bearer token and parses its body as JSON.
        /// Acquired per call, not cached here: <see cref="_credential"/> already caches the token
        /// itself, the same note <see cref="AzureCostProvider"/> documents on its own SendAsync.
        /// </summary>
        /// <exception cref="AppServiceHttpException">The response was not a success status.</exception>
        private async Task<JsonDocument> SendAsync(string siteName, string resourceGroup, string subscriptionId, CancellationToken ct)
        {
            var token = await _credential.GetTokenAsync(ManagementScope, ct).ConfigureAwait(false);

            var relativeUrl =
                $"subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/sites/{siteName}?api-version={ApiVersion}";

            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token.Token) }
            };

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new AppServiceHttpException(response.StatusCode);

                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
        }

        /// <summary>Internal control-flow signal for a non-success HTTP status -- never escapes
        /// <see cref="GetStatusAsync"/>, which maps it to <see cref="AppServiceState.NotAuthorized"/>
        /// (401/403) or <see cref="AppServiceState.Unavailable"/> (anything else).</summary>
        private sealed class AppServiceHttpException(HttpStatusCode statusCode) : Exception
        {
            public HttpStatusCode StatusCode { get; } = statusCode;
        }
    }
}
