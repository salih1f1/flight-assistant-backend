using System.Globalization;
using System.Net;
using System.Text;
using Azure.Core;
using FlightAssistant.Infrastructure.ExternalServices.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FlightAssistant.Application.Usage;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Azure;

/// <summary>
/// Drives <see cref="AzureCostProvider"/> against a stub <see cref="HttpMessageHandler"/> --
/// following the pattern in <c>AeroApiFlightStatusProviderTests</c> -- so every state the admin
/// budget card can show (#496 task 2) is pinned without ever calling Azure.
///
/// <para>
/// <b>dataAsOf (verified, not assumed).</b> The implementation plan's test for
/// <c>DataAsOfUtc</c> assumed the Consumption Budgets response (2023-05-01) carries a
/// <c>dataAsOf</c> field. It does not: Microsoft's REST API reference tables for
/// <c>BudgetsListResult</c>/<c>Budget</c> (2023-05-01) and <c>QueryResult</c> (Cost Management
/// Query, 2023-03-01) enumerate every field either response can return, and neither lists a
/// last-refreshed timestamp anywhere -- not top-level, not nested under <c>properties</c>. The
/// closest candidate, <c>eTag</c>, is documented as an opaque concurrency token
/// ("1d34d012214157f"), not a time. So <see cref="DataAsOfUtc_IsAlwaysNull_NeitherAzureEndpointReportsIt"/>
/// replaces the originally-planned "comes from the response, not the clock" test: there is no
/// response field to source a non-null value from, so the honest, tested behaviour is "always
/// null" -- which is exactly what <see cref="AMissingAsOfTimestamp_IsNull_NotNow"/> (unchanged
/// from the plan) already expects the panel to render as "age unknown".
/// </para>
/// </summary>
public class AzureCostProviderTests
{
    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond, List<HttpRequestMessage> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            calls.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Always succeeds instantly with a fixed token. Token ACQUISITION is Azure.Identity's
    /// own concern, not this provider's -- these tests exercise only what
    /// <see cref="AzureCostProvider"/> does with the HTTP response once it has one, so the
    /// credential itself is never real and never reaches the network.</summary>
    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("stub-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    private static (AzureCostProvider Provider, List<HttpRequestMessage> Calls) Build(
        AzureCostOptions? options = null,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var calls = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(respond ?? (_ => Json("{}")), calls);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") };

        var provider = new AzureCostProvider(
            http,
            Options.Create(options ?? new AzureCostOptions { SubscriptionId = "sub-1" }),
            new StubTokenCredential(),
            NullLogger<AzureCostProvider>.Instance);

        return (provider, calls);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string EmptyBudgets = """{ "value": [] }""";

    // Decimals are interpolated via InvariantCulture, not the default ToString(): the current
    // culture's decimal separator can be "," (this repo's own test run reproduced it), which
    // would silently split a JSON number into two array elements rather than throw.
    private static string BudgetWith(decimal amount, decimal spend, string currency) => $$"""
        { "value": [ { "name": "monthly", "properties": {
            "amount": {{amount.ToString(CultureInfo.InvariantCulture)}}, "currentSpend": { "amount": {{spend.ToString(CultureInfo.InvariantCulture)}}, "unit": "{{currency}}" } } } ] }
        """;

    /// <summary>Shaped as the Cost Management query API actually answers (verified against
    /// 2023-03-01's <c>QueryResult</c>): <c>properties.columns</c> naming the columns,
    /// <c>properties.rows</c> holding the values in the same order.</summary>
    private static string CostQueryWith(decimal cost, string currency) => $$"""
        { "properties": { "columns": [ { "name": "PreTaxCost", "type": "Number" }, { "name": "Currency", "type": "String" } ],
          "rows": [ [ {{cost.ToString(CultureInfo.InvariantCulture)}}, "{{currency}}" ] ] } }
        """;

    [Fact]
    public async Task NoSubscriptionId_IsNotConfigured_AndMakesNoCall()
    {
        var (provider, calls) = Build(options: new AzureCostOptions(), respond: _ => Json("{}"));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(AzureBudgetState.NotConfigured, snapshot.State);
        // A local developer has no subscription. Reaching out anyway would log a confusing failure
        // for a state that is entirely expected.
        Assert.Equal(0, calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusal_IsNotAuthorized_AndNamesTheRole(HttpStatusCode status)
    {
        var (provider, _) = Build(respond: _ => new HttpResponseMessage(status));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(AzureBudgetState.NotAuthorized, snapshot.State);
        Assert.Contains("Cost Management Reader", snapshot.Message);
    }

    // The constraint this whole feature rests on: being refused must never look like spending nothing.
    [Fact]
    public async Task ARefusal_ReportsNoSpendFigureAtAll()
    {
        var (provider, _) = Build(respond: _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Null(snapshot.CurrentSpendAmount);
        Assert.Null(snapshot.BudgetAmount);
    }

    [Fact]
    public async Task ABudget_IsRead_WithItsAmountSpendAndPeriod()
    {
        var (provider, _) = Build(respond: _ => Json("""
            { "value": [ { "name": "monthly",
                "properties": {
                  "amount": 50.0,
                  "timePeriod": { "startDate": "2026-09-01T00:00:00Z", "endDate": "2026-09-30T23:59:59Z" },
                  "currentSpend": { "amount": 12.34, "unit": "USD" } } } ] }
            """));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(AzureBudgetState.Ok, snapshot.State);
        Assert.Equal("monthly", snapshot.BudgetName);
        Assert.Equal(50.0m, snapshot.BudgetAmount);
        Assert.Equal(12.34m, snapshot.CurrentSpendAmount);
        Assert.Equal("USD", snapshot.Currency);
    }

    // The billing currency is a property of the account. Formatting EUR as dollars would misstate the
    // figure by a meaningful factor.
    [Fact]
    public async Task ANonUsdBudget_KeepsItsOwnCurrency()
    {
        var (provider, _) = Build(respond: _ => Json("""
            { "value": [ { "name": "monthly", "properties": {
                "amount": 40.0, "currentSpend": { "amount": 9.5, "unit": "EUR" } } } ] }
            """));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal("EUR", snapshot.Currency);
    }

    [Fact]
    public async Task NoBudgetDefined_IsNoBudget_NotUnavailable()
    {
        var (provider, _) = Build(respond: _ => Json("""{ "value": [] }"""));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(AzureBudgetState.NoBudget, snapshot.State);
    }

    // The cost query is the only reason a second call exists: with no budget there is no currentSpend
    // to read, and "no budget defined" on its own tells an operator nothing about spend.
    [Fact]
    public async Task NoBudgetDefined_StillReportsSpend_FromTheCostQuery()
    {
        var (provider, _) = Build(respond: req =>
            req.RequestUri!.AbsolutePath.Contains("budgets")
                ? Json(EmptyBudgets)
                : Json(CostQueryWith(12.34m, "USD")));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(AzureBudgetState.NoBudget, snapshot.State);
        Assert.Equal(12.34m, snapshot.CurrentSpendAmount);
        Assert.Equal("USD", snapshot.Currency);
    }

    // With a budget in hand its own currentSpend is authoritative, so a second call is wasted quota
    // against an endpoint Microsoft documents as low-throughput.
    [Fact]
    public async Task ABudgetWithSpend_DoesNotAlsoRunTheCostQuery()
    {
        var (provider, calls) = Build(respond: _ => Json(BudgetWith(50.0m, 12.34m, "USD")));

        await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task ANamedBudget_IsPreferredOverTheFirst()
    {
        var options = new AzureCostOptions { SubscriptionId = "sub-1", BudgetName = "production" };
        var (provider, _) = Build(options, respond: _ => Json("""
            { "value": [
                { "name": "dev", "properties": { "amount": 5.0, "currentSpend": { "amount": 1.0, "unit": "USD" } } },
                { "name": "production", "properties": { "amount": 500.0, "currentSpend": { "amount": 99.0, "unit": "USD" } } } ] }
            """));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal("production", snapshot.BudgetName);
        Assert.Equal(500.0m, snapshot.BudgetAmount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AnUpstreamFailure_IsUnavailable(HttpStatusCode status)
    {
        var (provider, _) = Build(respond: _ => new HttpResponseMessage(status));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(AzureBudgetState.Unavailable, snapshot.State);
    }

    [Fact]
    public async Task ABodyWeCannotRead_IsUnavailable_NotACrash()
    {
        var (provider, _) = Build(respond: _ => Json("not json at all"));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(AzureBudgetState.Unavailable, snapshot.State);
    }

    // An upstream message can name an internal address or quote raw body content; admins read this
    // field in every environment. Same rule VerificationSourceDownloader sets.
    [Fact]
    public async Task AnUpstreamMessage_NeverReachesTheOperatorFacingText()
    {
        var (provider, _) = Build(respond: _ => throw new HttpRequestException("connect failed to 10.0.0.7:443"));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(AzureBudgetState.Unavailable, snapshot.State);
        Assert.DoesNotContain("10.0.0.7", snapshot.Message ?? "");
    }

    // REPLACES the plan's "DataAsOfUtc_ComesFromTheResponse_NotTheClock". That test assumed a
    // "dataAsOf" field on the budgets response; verified against Microsoft's 2023-05-01 and
    // 2023-03-01 REST references, no such field exists anywhere in either response (see the class
    // doc). So the tested, honest behaviour is "always null" -- even against a response as fully
    // populated as this one (forecastSpend, a real eTag) -- rather than "read from a field that
    // does not exist". Never backfilled from a clock either: a fabricated freshness claim would be
    // worse than the "age unknown" the panel already renders for null.
    [Fact]
    public async Task DataAsOfUtc_IsAlwaysNull_NeitherAzureEndpointReportsIt()
    {
        var (provider, _) = Build(respond: _ => Json("""
            { "value": [ { "name": "monthly", "eTag": "\"x\"", "properties": {
                "amount": 50.0,
                "timePeriod": { "startDate": "2026-09-01T00:00:00Z", "endDate": "2026-09-30T23:59:59Z" },
                "currentSpend": { "amount": 12.34, "unit": "USD" },
                "forecastSpend": null } } ] }
            """));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Null(snapshot.DataAsOfUtc);
    }

    [Fact]
    public async Task AMissingAsOfTimestamp_IsNull_NotNow()
    {
        var (provider, _) = Build(respond: _ => Json("""
            { "value": [ { "name": "monthly", "properties": {
                "amount": 50.0, "currentSpend": { "amount": 12.34, "unit": "USD" } } } ] }
            """));

        var snapshot = await provider.GetBudgetAsync(CancellationToken.None);

        Assert.Null(snapshot.DataAsOfUtc);
    }
}
