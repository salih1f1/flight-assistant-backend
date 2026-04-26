using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
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
                case ValidationException ve:
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
                    await ctx.Response.WriteAsJsonAsync(domainProblem, ct);
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
