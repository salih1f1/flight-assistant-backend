using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The eight CSV export routes, which had no test at all before #414 — so nothing pinned what they
/// send, and nothing would have noticed the spec saying only that a 200 happens.
///
/// Both halves are checked against each other: what the routes actually return, and what the
/// document promises they return. Either alone can go quietly wrong — a declaration nothing sends,
/// or a response nothing declares.
/// </summary>
[Collection("Integration")]
public class AdminExportEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    /// <summary>Route under /admin/export, and the download filename it names in Content-Disposition.</summary>
    public static TheoryData<string, string> ExportRoutes => new()
    {
        { "airports", "airports-export.csv" },
        { "airlines", "airlines-export.csv" },
        { "alliances", "alliances-export.csv" },
        { "countries", "countries-export.csv" },
        { "lounges", "lounges-export.csv" },
        { "lounge-providers", "lounge-providers-export.csv" },
        { "loyalty-programs", "loyalty-programs-export.csv" },
        { "loyalty-program-kinds", "loyalty-program-kinds-export.csv" },
    };

    [Theory]
    [MemberData(nameof(ExportRoutes))]
    public async Task Export_SendsACsvFileUnderTheNameTheSpecPromises(string route, string fileName)
    {
        var response = await factory.CreateAdminClient().GetAsync($"admin/export/{route}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(fileName, response.Content.Headers.ContentDisposition?.FileNameStar
                               ?? response.Content.Headers.ContentDisposition?.FileName);
    }

    [Theory]
    [MemberData(nameof(ExportRoutes))]
    public void Export_DeclaresTheCsvFileItSends(string route, string fileName)
    {
        _ = fileName; // The filename is a response header, not part of the document.

        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        var ok = document.Paths[$"/admin/export/{route}"].Operations[OperationType.Get].Responses["200"];
        var body = Assert.Contains("text/csv", ok.Content);

        // A download, in the terms OpenAPI has for one — not an object schema, and not the bare
        // "200: OK" these routes declared before #414.
        Assert.Equal("string", body.Schema.Type);
        Assert.Equal("binary", body.Schema.Format);
    }
}
