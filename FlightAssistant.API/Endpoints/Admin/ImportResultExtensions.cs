using FlightAssistant.Application.Commands.Admin.Import;

namespace FlightAssistant.API.Endpoints.Admin;

public static class ImportResultExtensions
{
    public static IResult ToImportResponse(this ImportResult result) =>
        result.Errors.Count == 0
            ? Results.Ok(result)
            // No TypedResults helper exists for HTTP 207, so Results.Json is used directly.
            : Results.Json(result, statusCode: StatusCodes.Status207MultiStatus);
}
