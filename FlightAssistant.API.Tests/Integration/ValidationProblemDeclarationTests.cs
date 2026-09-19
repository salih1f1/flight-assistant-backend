using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// Pins what #506 fixed: a route that declares <c>.ProducesValidationProblem()</c> publishes a 400
/// carrying an <c>errors</c> dictionary, and nothing silently downgrades it to a bare
/// <c>ProblemDetails</c>.
///
/// <para>
/// The failure this exists to catch is invisible in the source: OpenAPI allows one response entry
/// per status, so declaring <c>.ProducesValidationProblem()</c> <b>and</b>
/// <c>.ProducesProblem(400)</c> on one route is legal C# in which the later call simply wins. Nine
/// routes — the eight CSV imports and <c>GET /admin/usage/aeroapi</c> — did exactly that, and the
/// published spec typed every one of those 400s as <c>ProblemDetails</c>. The routes really did
/// return the dictionary at runtime; only the contract every generated client is built from
/// disagreed, so <c>problem.errors[...]</c> was not on the type.
/// </para>
///
/// <para>
/// Like <see cref="AdminServerErrorDeclarationTests"/>, one side is derived from the running
/// application rather than written down: the document comes from the same
/// <c>ISwaggerProvider</c> that <c>scripts/generate-openapi.sh</c> exports, so this reads what the
/// code actually publishes. A second hand-maintained list would just be another thing to drift.
/// </para>
/// </summary>
[Collection("Integration")]
public class ValidationProblemDeclarationTests(FlightAssistantWebApplicationFactory factory)
{
    /// <summary>
    /// The schema Swashbuckle emits for <c>.ProducesValidationProblem()</c>. It extends
    /// <c>ProblemDetails</c> with the <c>errors</c> dictionary, which is the whole difference.
    /// </summary>
    private const string ValidationSchemaId = "HttpValidationProblemDetails";

    /// <summary>
    /// Every operation publishing a 400 whose schema is the plain <c>ProblemDetails</c>. That is
    /// legitimate on its own — a route whose only 400 comes from a <c>DomainException(…, 400)</c>
    /// has no <c>errors</c> dictionary to promise — so this is not asserted empty. It is asserted
    /// <b>stable</b>: an operation appearing here that a validator also guards is the #506
    /// regression, and a reviewer has to say which of the two it is.
    /// </summary>
    [Fact]
    public void NoImportOrUsageOperation_PublishesAPlainProblemDetails400()
    {
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        // The nine #506 corrected, by the path they are mapped on. Named rather than derived: the
        // point is that these specific operations, which all have validators, keep the dictionary.
        var mustCarryTheDictionary = document.Paths
            .Where(p => p.Key.StartsWith("/admin/import/", StringComparison.Ordinal)
                        || p.Key.Equals("/admin/usage/aeroapi", StringComparison.Ordinal)
                        || p.Key.Equals("/admin/usage/flight-data", StringComparison.Ordinal))
            .SelectMany(p => p.Value.Operations.Select(o => (Route: $"{o.Key.ToString().ToUpperInvariant()} {p.Key}", Operation: o.Value)))
            .ToList();

        // Guards the guard: if the routes are ever renamed, an empty set would pass vacuously.
        Assert.Equal(10, mustCarryTheDictionary.Count);

        var downgraded = mustCarryTheDictionary
            .Where(r => r.Operation.Responses.TryGetValue("400", out var response)
                        && SchemaIdOf(response) != ValidationSchemaId)
            .Select(r => $"{r.Route} -> {SchemaIdOf(r.Operation.Responses["400"]) ?? "(no schema)"}")
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(downgraded);
    }

    /// <summary>
    /// And the positive half: each of them really does declare a 400 at all. A route that dropped
    /// the declaration entirely would satisfy the check above by having nothing to downgrade.
    /// </summary>
    [Fact]
    public void EveryImportAndUsageOperation_DeclaresItsValidation400()
    {
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        var missing = document.Paths
            .Where(p => p.Key.StartsWith("/admin/import/", StringComparison.Ordinal)
                        || p.Key.StartsWith("/admin/usage/", StringComparison.Ordinal))
            .SelectMany(p => p.Value.Operations.Select(o => (Route: $"{o.Key.ToString().ToUpperInvariant()} {p.Key}", Operation: o.Value)))
            .Where(r => !r.Operation.Responses.ContainsKey("400"))
            .Select(r => r.Route)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    private static string? SchemaIdOf(OpenApiResponse response) =>
        response.Content.TryGetValue("application/problem+json", out var media)
            ? media.Schema?.Reference?.Id
            : null;
}
