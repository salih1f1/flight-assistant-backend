using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.Azure
{
    /// <summary>
    /// Reads the subscription's budget and month-to-date spend from Azure Cost Management: the
    /// Consumption Budgets list (<c>api-version=2023-05-01</c>) and, when the subscription has no
    /// budget, the Cost Management query (<c>api-version=2023-03-01</c>) -- the concrete
    /// <see cref="IAzureCostProvider"/> behind the admin dashboard's budget card (#496).
    ///
    /// <para>
    /// <b>Two endpoints, usually one call.</b> A budget's own <c>currentSpend</c> is authoritative
    /// once one exists, so <see cref="GetBudgetAsync"/> only falls back to the query when the
    /// budgets list comes back empty, or the one budget it selects carries no usable spend figure
    /// (an unevaluated budget, one whose period has not started, or a non-cost budget category) --
    /// calling it unconditionally would double the calls against an endpoint Microsoft documents as
    /// low-throughput, for a number the budgets call already carries in the common case.
    /// </para>
    ///
    /// <para>
    /// <b>DataAsOfUtc is always null here (verified, not assumed -- #496 task 2).</b> Neither
    /// response carries a last-refreshed timestamp: the Consumption Budgets List (2023-05-01) and
    /// Cost Management Query (2023-03-01) REST references each enumerate every field their response
    /// can return -- <c>BudgetsListResult</c>/<c>Budget</c>/<c>CurrentSpend</c>/<c>ForecastSpend</c>
    /// for the first, <c>QueryResult</c> for the second -- and neither lists one, at any nesting.
    /// <c>eTag</c> is the closest thing either carries, and it is documented as an opaque
    /// concurrency token ("1d34d012214157f"), not a time. So this never invents one: the field
    /// stays null, which the panel renders as "age unknown" rather than a fabricated freshness
    /// claim -- worse than not knowing, on a panel whose entire purpose is an honest cost warning.
    /// </para>
    /// </summary>
    public sealed class AzureCostProvider : IAzureCostProvider
    {
        /// <summary>The named <see cref="HttpClient"/> this provider resolves through --
        /// see <c>DependencyInjection.AddInfrastructure</c>.</summary>
        public const string HttpClientName = "AzureCost";

        private const string BudgetsApiVersion = "2023-05-01";
        private const string QueryApiVersion = "2023-03-01";

        // The aggregation's source column name, which the Cost Management query response echoes
        // back verbatim as a column header (verified against the 2023-03-01 examples -- the
        // dictionary KEY the request uses for an aggregation, e.g. "totalCost", is a request-side
        // alias only; the RESPONSE column is named after the aggregated column itself). "PreTaxCost"
        // is the Enterprise Agreement spelling; a Microsoft Customer Agreement or pay-as-you-go
        // subscription -- the likely case for this one -- names the same aggregation "Cost" or
        // "CostUSD" instead. This repository cannot tell which without Cost Management Reader
        // granted, so it accepts all three rather than betting on one: matching only "PreTaxCost"
        // would silently return no figure for the other two, which is this fallback's entire
        // reason to exist ("so the panel can still show a figure rather than nothing") failing
        // quietly. Matched case-insensitively against the column header (see GetCostQuerySpendAsync).
        private static readonly string[] CostColumnNames = ["PreTaxCost", "Cost", "CostUSD"];
        private const string CurrencyColumnName = "Currency";

        private static readonly TokenRequestContext ManagementScope = new(["https://management.azure.com/.default"]);

        private readonly HttpClient _httpClient;
        private readonly AzureCostOptions _options;
        private readonly TokenCredential _credential;
        private readonly ILogger<AzureCostProvider> _logger;

        public AzureCostProvider(
            HttpClient httpClient,
            IOptions<AzureCostOptions> options,
            TokenCredential credential,
            ILogger<AzureCostProvider> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _credential = credential;
            _logger = logger;
        }

        /// <summary>
        /// See <see cref="IAzureCostProvider"/> -- never throws for an expected condition. A 401/403
        /// maps to <see cref="AzureBudgetState.NotAuthorized"/> with <b>no</b> spend figure, not a
        /// zero: rendering a refusal as $0.00 would quietly tell an operator everything is fine, on
        /// a panel whose entire purpose is to warn about cost.
        /// </summary>
        public async Task<AzureBudgetSnapshot> GetBudgetAsync(CancellationToken ct)
        {
            if (!_options.IsConfigured)
                return AzureBudgetSnapshot.NotConfigured();

            try
            {
                using var budgetsDoc = await SendAsync(
                    HttpMethod.Get,
                    $"subscriptions/{_options.SubscriptionId}/providers/Microsoft.Consumption/budgets?api-version={BudgetsApiVersion}",
                    body: null,
                    ct).ConfigureAwait(false);

                var budget = SelectBudget(budgetsDoc.RootElement);
                if (budget is null)
                {
                    // No budget to report a figure against -- "no budget defined" alone tells an
                    // operator nothing about spend, so this is the one case worth a second call.
                    var (spend, currency) = await GetCostQuerySpendAsync(ct).ConfigureAwait(false);
                    return new AzureBudgetSnapshot(
                        AzureBudgetState.NoBudget, null, null, spend, currency, null, null, null, null, null);
                }

                return await MapBudgetAsync(budget.Value, ct).ConfigureAwait(false);
            }
            catch (AzureCostHttpException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "Azure Cost Management refused the request with HTTP {StatusCode} -- the managed " +
                    "identity likely lacks Cost Management Reader.", (int)ex.StatusCode);
                return AzureBudgetSnapshot.NotAuthorized();
            }
            catch (AzureCostHttpException ex)
            {
                _logger.LogWarning("Azure Cost Management returned HTTP {StatusCode}.", (int)ex.StatusCode);
                return AzureBudgetSnapshot.Unavailable($"Azure Cost Management returned HTTP {(int)ex.StatusCode}.");
            }
            catch (AzureCostBudgetNotFoundException ex)
            {
                // A typo'd or renamed AzureCost:BudgetName must not silently fall back to whichever
                // budget Azure happens to list first -- a $5 "dev" budget shown in place of a $500
                // "production" one is worse than an honest "not found".
                _logger.LogWarning(
                    "AzureCost:BudgetName '{BudgetName}' does not match any budget Azure returned.",
                    ex.BudgetName);
                return AzureBudgetSnapshot.Unavailable(
                    $"Configured budget '{ex.BudgetName}' (AzureCost:BudgetName) was not found in Azure.");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Azure Cost Management returned a response that could not be parsed.");
                return AzureBudgetSnapshot.Unavailable("Azure Cost Management returned a response that could not be read.");
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // The deadline elapsed, not the caller -- genuine caller cancellation (guarded by
                // the `when`) is left to propagate rather than being reported as a budget state,
                // same distinction VerificationSourceDownloader draws.
                _logger.LogWarning(ex, "Azure Cost Management call timed out.");
                return AzureBudgetSnapshot.Unavailable("Timed out calling Azure Cost Management.");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Azure Cost Management call failed (network error).");
                return AzureBudgetSnapshot.Unavailable("Could not reach Azure Cost Management (network error).");
            }
            catch (AuthenticationFailedException ex)
            {
                // DefaultAzureCredential could not obtain a token at all -- e.g. no managed identity
                // reachable locally. Distinct from a 401/403 FROM Azure: this never reached Azure.
                _logger.LogWarning(ex, "Could not authenticate to Azure for Cost Management.");
                return AzureBudgetSnapshot.Unavailable("Could not authenticate to Azure.");
            }
        }

        /// <summary>Picks the budget named by <see cref="AzureCostOptions.BudgetName"/> when set,
        /// else the first well-formed entry in <c>value</c>. Null when <c>value</c> is empty or
        /// absent, or none of its entries carry both a <c>name</c> and <c>properties</c>.</summary>
        /// <exception cref="AzureCostBudgetNotFoundException"><see cref="AzureCostOptions.BudgetName"/>
        /// is set, at least one well-formed budget came back, and none of them carry that name --
        /// silently falling back to <c>first</c> here would report a different budget's figures
        /// under a name nobody asked for.</exception>
        private (string Name, JsonElement Properties)? SelectBudget(JsonElement root)
        {
            if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                return null;

            (string Name, JsonElement Properties)? first = null;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty("properties", out var properties)) continue;

                var name = nameEl.GetString()!;
                first ??= (name, properties);

                if (_options.BudgetName is { Length: > 0 } wanted && string.Equals(name, wanted, StringComparison.Ordinal))
                    return (name, properties);
            }

            if (_options.BudgetName is { Length: > 0 } configuredName && first is not null)
                throw new AzureCostBudgetNotFoundException(configuredName);

            return first;
        }

        /// <summary>
        /// Maps a budget entry to <see cref="AzureBudgetState.Ok"/> -- but only once it actually
        /// has a labelled spend figure to show. A budget Azure has not yet evaluated, one whose
        /// <c>timePeriod.startDate</c> is in the future, or a <c>ReservationUtilization</c>-category
        /// budget (the same list endpoint returns those alongside cost budgets) all carry a `name`
        /// and `properties` with no usable `currentSpend`. Reporting `Ok` anyway would have the
        /// panel draw a gauge from a null spend -- "$0.00 of $50.00" -- with no 403 anywhere: the
        /// exact failure this feature exists to prevent, through a different door. So a missing
        /// spend, or a spend with no currency to safely label it as, falls through to the cost
        /// query for a figure; if that yields nothing usable either, this reports
        /// <see cref="AzureBudgetState.Unavailable"/> rather than a spend-less (or unlabelled) `Ok`.
        /// </summary>
        private async Task<AzureBudgetSnapshot> MapBudgetAsync(
            (string Name, JsonElement Properties) budget, CancellationToken ct)
        {
            var (name, properties) = budget;

            decimal? amount = properties.TryGetProperty("amount", out var amountEl) && amountEl.ValueKind == JsonValueKind.Number
                ? amountEl.GetDecimal()
                : null;

            var (spend, currency) = ReadCurrentSpend(properties);

            if (spend is null || currency is null)
                (spend, currency) = await GetCostQuerySpendAsync(ct).ConfigureAwait(false);

            if (spend is null || currency is null)
                return AzureBudgetSnapshot.Unavailable(
                    "Azure Cost Management reported a budget with no spend figure.");

            var (periodStart, periodEnd) = ReadPeriod(properties);

            // How often the budget resets. Reported rather than inferred from the period: a budget's
            // validity window can span years while the spend figure covers one month (#525).
            var timeGrain = properties.TryGetProperty("timeGrain", out var grain) && grain.ValueKind == JsonValueKind.String
                ? grain.GetString()
                : null;

            return new AzureBudgetSnapshot(
                AzureBudgetState.Ok, name, amount, spend, currency, timeGrain, periodStart, periodEnd, DataAsOfUtc: null, Message: null);
        }

        /// <summary>The budget's own <c>currentSpend</c>, when it carries one. Split out from
        /// <see cref="MapBudgetAsync"/> so that method's spend-or-currency-missing check reads as
        /// one condition instead of four scattered null tests.</summary>
        private static (decimal? Spend, string? Currency) ReadCurrentSpend(JsonElement properties)
        {
            decimal? spend = null;
            string? currency = null;
            if (properties.TryGetProperty("currentSpend", out var currentSpend) && currentSpend.ValueKind == JsonValueKind.Object)
            {
                if (currentSpend.TryGetProperty("amount", out var spendEl) && spendEl.ValueKind == JsonValueKind.Number)
                    spend = spendEl.GetDecimal();
                if (currentSpend.TryGetProperty("unit", out var unitEl) && unitEl.ValueKind == JsonValueKind.String)
                    currency = unitEl.GetString();
            }
            return (spend, currency);
        }

        private static (DateTimeOffset? Start, DateTimeOffset? End) ReadPeriod(JsonElement properties)
        {
            if (!properties.TryGetProperty("timePeriod", out var timePeriod) || timePeriod.ValueKind != JsonValueKind.Object)
                return (null, null);

            return (ReadDate(timePeriod, "startDate"), ReadDate(timePeriod, "endDate"));

            static DateTimeOffset? ReadDate(JsonElement obj, string field) =>
                obj.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : null;
        }

        /// <summary>
        /// The subscription's month-to-date spend when it has no budget to read one from. Minimal
        /// by design (YAGNI): total cost only, no grouping/breakdown -- the panel has nowhere to
        /// show one, and this endpoint is documented as low-throughput.
        /// </summary>
        private async Task<(decimal? Spend, string? Currency)> GetCostQuerySpendAsync(CancellationToken ct)
        {
            const string requestBody = """
                { "type": "ActualCost", "timeframe": "MonthToDate",
                  "dataset": { "granularity": "None",
                    "aggregation": { "totalCost": { "name": "PreTaxCost", "function": "Sum" } } } }
                """;

            using var doc = await SendAsync(
                HttpMethod.Post,
                $"subscriptions/{_options.SubscriptionId}/providers/Microsoft.CostManagement/query?api-version={QueryApiVersion}",
                requestBody,
                ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("properties", out var properties)
                || !properties.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array
                || !properties.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return (null, null);

            var costIndex = -1;
            var currencyIndex = -1;
            var index = 0;
            foreach (var column in columns.EnumerateArray())
            {
                var columnName = column.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (columnName is not null && CostColumnNames.Contains(columnName, StringComparer.OrdinalIgnoreCase)) costIndex = index;
                else if (string.Equals(columnName, CurrencyColumnName, StringComparison.Ordinal)) currencyIndex = index;
                index++;
            }

            if (costIndex < 0)
                return (null, null);

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array) continue;

                var cells = row.EnumerateArray().ToList();
                if (costIndex >= cells.Count) continue;

                decimal? spend = cells[costIndex].ValueKind == JsonValueKind.Number ? cells[costIndex].GetDecimal() : null;
                string? currency = currencyIndex >= 0 && currencyIndex < cells.Count && cells[currencyIndex].ValueKind == JsonValueKind.String
                    ? cells[currencyIndex].GetString()
                    : null;

                return (spend, currency);
            }

            return (null, null);
        }

        /// <summary>
        /// Sends one request with a freshly-acquired bearer token and parses its body as JSON.
        /// Acquired per call, not cached here: <paramref name="credential"/>-&gt;
        /// <see cref="_credential"/> already caches the token itself (context note, #496 task 2),
        /// so there is nothing to gain from a second cache layered on top.
        /// </summary>
        /// <exception cref="AzureCostHttpException">The response was not a success status.</exception>
        private async Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, string? body, CancellationToken ct)
        {
            var token = await _credential.GetTokenAsync(ManagementScope, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(method, relativeUrl)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token.Token) }
            };
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new AzureCostHttpException(response.StatusCode);

                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
        }

        /// <summary>Internal control-flow signal for a non-success HTTP status -- never escapes
        /// <see cref="GetBudgetAsync"/>, which maps it to <see cref="AzureBudgetState.NotAuthorized"/>
        /// (401/403) or <see cref="AzureBudgetState.Unavailable"/> (anything else).</summary>
        private sealed class AzureCostHttpException(HttpStatusCode statusCode) : Exception
        {
            public HttpStatusCode StatusCode { get; } = statusCode;
        }

        /// <summary>Internal control-flow signal from <see cref="SelectBudget"/> -- never escapes
        /// <see cref="GetBudgetAsync"/>, which maps it to <see cref="AzureBudgetState.Unavailable"/>
        /// naming the configured value.</summary>
        private sealed class AzureCostBudgetNotFoundException(string budgetName) : Exception
        {
            public string BudgetName { get; } = budgetName;
        }
    }
}
