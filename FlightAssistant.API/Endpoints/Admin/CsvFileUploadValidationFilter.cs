using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Endpoints.Admin;

/// <summary>
/// Validates that the uploaded file on a CSV import endpoint is a text/csv file
/// within the allowed size limit, before the endpoint handler runs.
/// </summary>
public class CsvFileUploadValidationFilter : IEndpointFilter
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var file = context.Arguments.OfType<IFormFile>().FirstOrDefault();

        if (file is not null)
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Only text/csv files are accepted.",
                    type: "https://tools.ietf.org/html/rfc7807",
                    instance: context.HttpContext.Request.Path);
            }

            if (file.Length > MaxFileSizeBytes)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "File size exceeds the 10 MB limit.",
                    type: "https://tools.ietf.org/html/rfc7807",
                    instance: context.HttpContext.Request.Path);
            }
        }

        return await next(context);
    }
}
