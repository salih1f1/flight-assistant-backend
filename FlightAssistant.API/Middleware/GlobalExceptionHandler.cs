using CsvHelper;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Middleware
{
    public class GlobalExceptionHandler : IExceptionHandler
    {
        private readonly ILogger<GlobalExceptionHandler> _log;

        public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log) => _log = log;

        public async ValueTask<bool> TryHandleAsync(
            HttpContext ctx, Exception ex, CancellationToken ct)
        {
            switch (ex)
            {
                case FluentValidation.ValidationException ve:
                    var errors = ve.Errors
                        .GroupBy(f => f.PropertyName)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(f => f.ErrorMessage).ToArray());

                    var validationProblem = new ValidationProblemDetails(errors)
                    {
                        Status = StatusCodes.Status400BadRequest,
                        Title = "Validation failed",
                        Instance = ctx.Request.Path
                    };

                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(validationProblem, ct);
                    return true;

                case DomainException de:
                    var domainProblem = new ProblemDetails
                    {
                        Status = de.StatusCode,
                        Title = de.Message,
                        Instance = ctx.Request.Path
                    };

                    ctx.Response.StatusCode = de.StatusCode;
                    // For upstream throttling (503), hint clients when to retry.
                    if (de.StatusCode == StatusCodes.Status503ServiceUnavailable)
                        ctx.Response.Headers.RetryAfter = "60";
                    await ctx.Response.WriteAsJsonAsync(domainProblem, ct);
                    return true;

                case CsvHelperException csvEx:
                    var csvProblem = new ProblemDetails
                    {
                        Status = StatusCodes.Status400BadRequest,
                        Title = "Invalid CSV file",
                        Detail = csvEx.Message,
                        Instance = ctx.Request.Path,
                        Type = "https://tools.ietf.org/html/rfc7807"
                    };

                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(csvProblem, ct);
                    return true;

                default:
                    _log.LogError(ex, "Unhandled exception on {Method} {Path}",
                        ctx.Request.Method, ctx.Request.Path);

                    var serverProblem = new ProblemDetails
                    {
                        Status = StatusCodes.Status500InternalServerError,
                        Title = "An unexpected error occurred.",
                        Instance = ctx.Request.Path
                    };

                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsJsonAsync(serverProblem, ct);
                    return true;
            }
        }
    }
}
