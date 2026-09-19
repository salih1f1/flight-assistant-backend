using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// Pins the invariant #416 unlocks: on the admin surface, <b>500 is declared iff a handler throws
/// it</b>. CLAUDE.md's rule is that ambient statuses — the 401, the AdminOnly 403, the 429, and
/// <c>GlobalExceptionHandler</c>'s <c>default:</c> arm, which every one of the admin operations can
/// reach — are never declared per route. A 500 that appears here without a
/// <c>DomainException(…, 500)</c> behind it is that rule being broken.
///
/// One side is derived from the running application rather than written down: the document comes
/// from the same <c>ISwaggerProvider</c> that <c>scripts/generate-openapi.sh</c> exports, so the
/// test reads what the code actually declares, not what a second list says it declares.
/// </summary>
[Collection("Integration")]
public class AdminServerErrorDeclarationTests(FlightAssistantWebApplicationFactory factory)
{
    /// <summary>
    /// Every admin operation whose handler throws a <c>DomainException(…, 500)</c> of its own, and
    /// therefore the complete set allowed to declare one -- with one deliberate, reviewed exception
    /// called out on its own bullet below rather than left silent. Each of the rest is a provider
    /// credential or configuration failure the operation cannot degrade past:
    /// <list type="bullet">
    /// <item><c>/admin/ci/runs</c>, <c>/admin/ci/runs/{id}/tests</c> — GitHub Actions token missing
    /// or rejected (<c>GetCiRunsQuery</c>, <c>GetCiRunTestsQuery</c>).</item>
    /// <item><c>/admin/flights/search</c>, <c>/admin/flights/{id}/sync</c> — AeroAPI key missing or
    /// rejected (<c>SearchFlightsQuery</c>, <c>SyncFlightStatusCommand</c>).</item>
    /// <item><c>/admin/usage/aeroapi</c> — same key, on the usage route.</item>
    /// <item><c>/admin/usage/azure</c> — the one exception: not a <c>DomainException</c>.
    /// <c>AzureBudgetCache.GetOrFetchAsync</c> calls <c>ICacheService</c> with no try/catch, and
    /// <c>RedisCacheService</c> rethrows on a Redis outage, reaching <c>GlobalExceptionHandler</c>'s
    /// ambient default arm like any unhandled exception would. Declared anyway (final review before
    /// #496's pull request, 2026-09-19) so a generated client has an error branch for a failure this
    /// route can genuinely reach -- a deliberate, reviewed exception to this list's rule, spelled
    /// out here rather than silent.</item>
    /// </list>
    /// A new entry here is ordinarily a claim that the operation throws its own 500 -- check that it
    /// does before adding one, or the alternative is that the ambient 500 has crept back in under a
    /// new name. <c>/admin/usage/azure</c> above is the one deliberate exception to that rule.
    /// </summary>
    private static readonly string[] ThrowTheirOwnServerError =
    [
        "GET /admin/ci/runs",
        "GET /admin/ci/runs/{id}/tests",
        "GET /admin/flights/search",
        "GET /admin/usage/aeroapi",
        "GET /admin/usage/azure",
        "POST /admin/flights/{id}/sync",
    ];

    [Fact]
    public void AdminOperationsDeclaringServerError_AreExactlyThoseThatThrowOne()
    {
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        var declared = DeclaredServerErrors(document);

        Assert.Equal(ThrowTheirOwnServerError, declared);
    }

    /// <summary>
    /// The route the invariant was broken on (#416). Called out separately from the set above so a
    /// regression here names the health report rather than printing a diff of five other routes:
    /// its contract is that the report is <b>always</b> 200 and the overall status lives in the
    /// payload, precisely so the panel's error handling cannot swallow the body that explains what
    /// broke. Declaring a 500 invites the error branch that design exists to prevent.
    /// </summary>
    [Fact]
    public void AdminHealth_DeclaresOnlyTheReportItAlwaysReturns()
    {
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        var health = document.Paths["/admin/health"].Operations[OperationType.Get];

        Assert.Equal(["200"], health.Responses.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    private static string[] DeclaredServerErrors(OpenApiDocument document) =>
        [.. document.Paths
            .Where(path => path.Key.StartsWith("/admin", StringComparison.Ordinal))
            .SelectMany(path => path.Value.Operations.Select(op => (path.Key, Method: op.Key, Operation: op.Value)))
            .Where(route => route.Operation.Responses.ContainsKey("500"))
            .Select(route => $"{route.Method.ToString().ToUpperInvariant()} {route.Key}")
            .OrderBy(route => route, StringComparer.Ordinal)];
}
