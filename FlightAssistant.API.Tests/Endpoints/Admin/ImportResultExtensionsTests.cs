using FlightAssistant.API.Endpoints.Admin;
using FlightAssistant.Application.Commands.Admin.Import;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FlightAssistant.API.Tests.Endpoints.Admin;

public class ImportResultExtensionsTests
{
    [Fact]
    public void ToImportResponse_NoErrors_ReturnsOk200()
    {
        var result = new ImportResult(Inserted: 2, Updated: 1, Errors: []);

        var response = result.ToImportResponse();

        var ok = Assert.IsType<Ok<ImportResult>>(response);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
        Assert.Same(result, ok.Value);
    }

    [Fact]
    public void ToImportResponse_PartialErrors_ReturnsJson207()
    {
        var result = new ImportResult(Inserted: 1, Updated: 0, Errors: ["Row 2: Unknown airport ZZZ"]);

        var response = result.ToImportResponse();

        var json = Assert.IsType<JsonHttpResult<ImportResult>>(response);
        Assert.Equal(StatusCodes.Status207MultiStatus, json.StatusCode);
        Assert.Same(result, json.Value);
    }

    [Fact]
    public void ToImportResponse_AllRowsFailed_ReturnsJson207WithNoInsertsOrUpdates()
    {
        var errors = new[] { "Row 1: invalid code", "Row 2: invalid code" };
        var result = new ImportResult(Inserted: 0, Updated: 0, Errors: errors);

        var response = result.ToImportResponse();

        var json = Assert.IsType<JsonHttpResult<ImportResult>>(response);
        Assert.Equal(StatusCodes.Status207MultiStatus, json.StatusCode);
        Assert.Equal(0, json.Value!.Inserted);
        Assert.Equal(0, json.Value!.Updated);
        Assert.Equal(errors.Length, json.Value!.Errors.Count);
    }
}
